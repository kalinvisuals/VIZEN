using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using VIZEN.App.Interop;
using VIZEN.App.Models;

namespace VIZEN.App.Services;

/// <summary>An output device the sound of an app can be sent to ("listen on").</summary>
public sealed record AudioOutputDevice(string Id, string Name);

/// <summary>
/// Per-app sound through the Windows audio session API (Core Audio / WASAPI, no injection). One scan reads the audio
/// sessions of every active output device and groups them by executable (so helper processes such as Chrome's audio
/// service count for the browser); it serves both the sound controls on the source cards and Smart Audio Mixer.
/// Volume and mute are per session (ISimpleAudioVolume, like the Windows volume mixer): the master volume is never
/// touched. Sessions are re-read every 2 s (1 s while the mixer is on screen) only while something shows sound
/// controls, and at once when an output device is added, removed or changes. UI thread only.
/// </summary>
public sealed class AppAudioService : IMixerSource, IDisposable
{
    private static readonly TimeSpan IdleScanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MixerScanInterval = TimeSpan.FromSeconds(1);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<uint, string?> _exeByPid = [];
    private readonly Dictionary<string, AppAudio> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioSessionControl> _sessionsById = [];
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _sessionTimer;
    private readonly DispatcherTimer _deviceDebounce;
    private List<MMDevice>? _devices;
    private MMDevice? _master;
    private bool _masterMissing;
    private float _masterVolume = -1;
    private bool _masterMuted;
    private List<IMixerApp> _mixerApps = [];
    private AudioDeviceNotifications? _deviceEvents;
    private bool _mixerActive;
    private bool _focusActive;
    private readonly Stopwatch _meterClock = Stopwatch.StartNew();
    private double _lastMeterMs;
    private bool _disposed;

    public AppAudioService()
    {
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(70), DispatcherPriority.Background, (_, _) => UpdateMeters(), _dispatcher);
        _sessionTimer = new DispatcherTimer(IdleScanInterval, DispatcherPriority.Background, (_, _) => RefreshSessions(), _dispatcher);
        _deviceDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _deviceDebounce.Stop();
            RescanNow();
            ResetMaster(); // the default output device may have changed
        }, _dispatcher);
        _meterTimer.Stop();
        _sessionTimer.Stop();
        _deviceDebounce.Stop();
    }

    /// <summary>True on Windows 10 2004 (build 19041) and later, where one app's sound can be captured on its own.</summary>
    public static bool IsListenSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>Completed scans (diagnostics and tests of the polling policy).</summary>
    public int ScanCount { get; private set; }

    public bool IsWatching => _sessionTimer.IsEnabled;

    public IReadOnlyList<AudioOutputDevice> OutputDevices()
    {
        try
        {
            return _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select((d, i) => new AudioOutputDevice(d.ID, DemoMode.IsOn ? $"Output device {i + 1}" : d.FriendlyName)).ToList();
        }
        catch (COMException)
        {
            return [];
        }
    }

    /// <summary>The shared audio controller for the app that owns <paramref name="processId"/> (null for cameras).</summary>
    public AppAudio? For(string? executablePath, uint processId, string appName)
    {
        if (string.IsNullOrEmpty(executablePath) || executablePath.StartsWith("camera:", StringComparison.Ordinal))
        {
            return null;
        }

        if (!_apps.TryGetValue(executablePath, out var app))
        {
            // The same product name as in the mixer ("Google Chrome"), whichever asks first.
            app = new AppAudio(this, executablePath, executablePath, DisplayNameOf(executablePath) is { Length: > 0 } name ? name : appName, isSystem: false);
            _apps[executablePath] = app;
        }

        app.ProcessId = processId;
        if (!app.IsTracked)
        {
            app.IsTracked = true;
            RescanNow();
            UpdateTimers();
        }

        return app;
    }

    // ----- Smart Audio Mixer -----

    public IReadOnlyList<IMixerApp> MixerApps => _mixerApps;

    public event EventHandler? MixerAppsChanged;

    public void SetMixerActive(bool active)
    {
        if (_mixerActive == active)
        {
            return;
        }

        _mixerActive = active;
        if (active)
        {
            RescanNow();
            UpdateMaster();
        }

        UpdateTimers();
    }

    public void SetFocusActive(bool active)
    {
        if (_focusActive == active)
        {
            return;
        }

        _focusActive = active;
        if (active)
        {
            RescanNow();
        }

        UpdateTimers();
    }

    public event EventHandler<double>? MeterTick;

    public void RescanNow()
    {
        if (!_disposed)
        {
            RefreshSessions();
        }
    }

    // ----- Windows master volume (default output device) -----

    public bool HasMaster => Master() is not null;

    public string? MasterDeviceName => DemoMode.IsOn ? null : Master() is { } device ? Read(() => device.FriendlyName, null) : null;

    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            if (Master() is { } device)
            {
                var volume = Math.Clamp(value, 0f, 1f);
                Read(() => device.AudioEndpointVolume.MasterVolumeLevelScalar = volume, 0f);
                _masterVolume = volume;
            }
        }
    }

    public bool MasterMuted
    {
        get => _masterMuted;
        set
        {
            if (Master() is { } device)
            {
                Read(() => device.AudioEndpointVolume.Mute = value, false);
                _masterMuted = value;
            }
        }
    }

    public float MasterPeak { get; private set; }

    public event EventHandler? MasterChanged;

    public event EventHandler? MasterPeakChanged;

    private MMDevice? Master()
    {
        if (_master is null && !_masterMissing)
        {
            _master = Device(null);
            _masterMissing = _master is null;
        }

        return _master;
    }

    /// <summary>Reads the master volume, mute and level (meter tick, only while the mixer is on screen).</summary>
    private void UpdateMaster()
    {
        if (Master() is not { } device)
        {
            return;
        }

        var volume = Read(() => device.AudioEndpointVolume.MasterVolumeLevelScalar, _masterVolume);
        var muted = Read(() => device.AudioEndpointVolume.Mute, _masterMuted);
        if (Math.Abs(volume - _masterVolume) > 0.004f || muted != _masterMuted)
        {
            (_masterVolume, _masterMuted) = (volume, muted);
            MasterChanged?.Invoke(this, EventArgs.Empty);
        }

        var peak = Read(() => device.AudioMeterInformation.MasterPeakValue, 0f);
        var shown = Math.Max(peak, MasterPeak * 0.72f);
        if (shown < 0.01f)
        {
            shown = 0;
        }

        if (Math.Abs(shown - MasterPeak) > 0.003f || (shown == 0 && MasterPeak != 0))
        {
            MasterPeak = shown;
            MasterPeakChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The default device may have changed: take it again and report it.</summary>
    private void ResetMaster()
    {
        _master = null;
        _masterMissing = false;
        _masterVolume = -1;
        UpdateMaster();
        MasterChanged?.Invoke(this, EventArgs.Empty);
    }

    public ImageSource? IconFor(IMixerApp app)
    {
        if (app.ExecutablePath is not { } path)
        {
            return null;
        }

        if (!_icons.TryGetValue(path, out var icon))
        {
            icon = ExecutableIcon.Load(path);
            _icons[path] = icon;
        }

        return icon;
    }

    /// <summary>Timers run only while sound controls are on screen: a card with an app's sound, or the mixer.</summary>
    private void UpdateTimers()
    {
        var watch = !_disposed && (_mixerActive || _focusActive || _apps.Values.Any(a => a.IsTracked));
        _sessionTimer.Interval = _mixerActive || _focusActive ? MixerScanInterval : IdleScanInterval;
        _sessionTimer.IsEnabled = watch;
        _meterTimer.IsEnabled = watch;
        if (watch && _deviceEvents is null)
        {
            // Without notifications (null) the periodic scan still notices devices, only later.
            _deviceEvents = AudioDeviceNotifications.TryStart(OnDeviceChangedFromAnyThread);
        }
    }

    /// <summary>Reads every session once and hands each app its sessions.</summary>
    internal void RefreshSessions()
    {
        var byKey = new Dictionary<string, List<AudioSessionControl>>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>();
        var seenPids = new HashSet<uint>();
        foreach (var device in Devices())
        {
            try
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var all = manager.Sessions;
                for (var i = 0; i < all.Count; i++)
                {
                    var fresh = all[i];
                    if (Read(() => fresh.State, AudioSessionState.AudioSessionStateExpired) == AudioSessionState.AudioSessionStateExpired)
                    {
                        fresh.Dispose();
                        continue;
                    }

                    var id = Read(() => fresh.GetSessionInstanceIdentifier, null) ?? $"{device.ID}#{i}";
                    if (!seenIds.Add(id))
                    {
                        fresh.Dispose();
                        continue;
                    }

                    // Keep one wrapper per session for its whole life instead of a new one on every scan.
                    if (_sessionsById.TryGetValue(id, out var known))
                    {
                        fresh.Dispose();
                    }
                    else
                    {
                        _sessionsById[id] = known = fresh;
                    }

                    var key = KeyOf(known, seenPids);
                    if (key is null || (key != AudioProfiles.SystemKey && !DemoMode.ShowsAudioApp(key)))
                    {
                        continue;
                    }

                    if (!byKey.TryGetValue(key, out var list))
                    {
                        byKey[key] = list = [];
                    }

                    list.Add(known);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
            {
                _devices = null; // a device went away while it was read: enumerate the devices again next time
            }
        }

        foreach (var gone in _sessionsById.Keys.Where(id => !seenIds.Contains(id)).ToList())
        {
            _sessionsById.Remove(gone, out var session);
            session?.Dispose();
        }

        foreach (var pid in _exeByPid.Keys.Where(p => !seenPids.Contains(p)).ToList())
        {
            _exeByPid.Remove(pid); // a reused process id must not inherit an old name
        }

        foreach (var (key, sessions) in byKey)
        {
            if (!_apps.TryGetValue(key, out var app))
            {
                app = CreateApp(key, sessions[0]);
                _apps[key] = app;
            }

            app.SetSessions(sessions);
        }

        foreach (var app in _apps.Values.Where(a => !byKey.ContainsKey(a.Id)))
        {
            app.SetSessions([]);
        }

        // Apps that are no longer tracked by a card and have no sound are forgotten.
        foreach (var key in _apps.Where(p => !p.Value.IsTracked && !p.Value.HasSessions && !p.Value.IsListening).Select(p => p.Key).ToList())
        {
            _apps.Remove(key);
        }

        var mixer = _apps.Values.Where(a => a.HasSessions)
            .OrderBy(a => a.IsSystemSounds)
            .ThenBy(a => a.AppName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Cast<IMixerApp>()
            .ToList();
        var changed = !mixer.SequenceEqual(_mixerApps);
        _mixerApps = mixer;
        ScanCount++;
        if (changed)
        {
            MixerAppsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>"system" for the Windows system sounds, the executable path, or "pid:N" when Windows hides the path.</summary>
    private string? KeyOf(AudioSessionControl session, HashSet<uint> seenPids)
    {
        if (Read(() => session.IsSystemSoundsSession, false))
        {
            return AudioProfiles.SystemKey;
        }

        var pid = Read(() => session.GetProcessID, 0u);
        if (pid == 0)
        {
            return null;
        }

        seenPids.Add(pid);
        return ExecutableOf(pid) ?? $"pid:{pid}";
    }

    private AppAudio CreateApp(string key, AudioSessionControl first)
    {
        if (key == AudioProfiles.SystemKey)
        {
            return new AppAudio(this, key, null, "System sounds", isSystem: true);
        }

        if (key.StartsWith("pid:", StringComparison.Ordinal))
        {
            var pid = int.Parse(key.AsSpan(4));
            string name;
            try
            {
                using var process = Process.GetProcessById(pid);
                name = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                name = Read(() => first.DisplayName, null) is { Length: > 0 } display && !display.StartsWith('@') ? display : key;
            }

            return new AppAudio(this, key, null, name, isSystem: false);
        }

        return new AppAudio(this, key, key, DisplayNameOf(key), isSystem: false);
    }

    /// <summary>The app's own product name ("Google Chrome", "Discord"), or its file name without extension.</summary>
    private static string DisplayNameOf(string executablePath)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(executablePath).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description))
            {
                return description;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // fall back to the file name
        }

        return Path.GetFileNameWithoutExtension(executablePath);
    }

    private List<MMDevice> Devices()
    {
        if (_devices is null)
        {
            try
            {
                _devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
            }
            catch (COMException)
            {
                return [];
            }
        }

        return _devices;
    }

    private void UpdateMeters()
    {
        foreach (var app in _apps.Values)
        {
            app.UpdatePeak();
        }

        if (_mixerActive)
        {
            UpdateMaster(); // Windows volume changes show up within a tick
        }

        var now = _meterClock.Elapsed.TotalMilliseconds;
        var elapsed = Math.Clamp(now - _lastMeterMs, 0, 500);
        _lastMeterMs = now;
        MeterTick?.Invoke(this, elapsed);
    }

    private string? ExecutableOf(uint pid)
    {
        if (_exeByPid.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        string? path = null;
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                var size = (uint)buffer.Capacity;
                if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    path = buffer.ToString();
                }
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        _exeByPid[pid] = path;
        return path;
    }

    internal MMDevice? Device(string? id)
    {
        try
        {
            return id is null
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : _enumerator.GetDevice(id);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Called by Windows on its own thread: re-enumerate the devices, then rescan once things settle.</summary>
    private void OnDeviceChangedFromAnyThread() => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed)
        {
            return;
        }

        _devices = null;
        _deviceDebounce.Stop();
        _deviceDebounce.Start();
    });

    internal static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ObjectDisposedException or InvalidComObjectException)
        {
            return fallback;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meterTimer.Stop();
        _sessionTimer.Stop();
        _deviceDebounce.Stop();
        _deviceEvents?.Dispose();

        foreach (var app in _apps.Values)
        {
            app.StopListening();
        }

        foreach (var session in _sessionsById.Values)
        {
            session.Dispose();
        }

        _sessionsById.Clear();
        _enumerator.Dispose();
    }
}

/// <summary>
/// The sound of one app: its volume and mute (applied to all its audio sessions, like the Windows volume mixer),
/// its current peak level, and optional "listen" — just this app's sound played on another output device.
/// </summary>
public sealed class AppAudio : IMixerApp
{
    private readonly AppAudioService _service;
    private List<AudioSessionControl> _sessions = [];
    private WasapiRecorder? _recorder;
    private WasapiPlayer? _player;
    private BufferedWaveProvider? _buffer;
    private float _lastVolume = -1;
    private bool _lastMuted;
    private int _lastActive = -1;

    internal AppAudio(AppAudioService service, string id, string? executablePath, string appName, bool isSystem)
    {
        _service = service;
        Id = id;
        ExecutablePath = executablePath;
        AppName = appName;
        IsSystemSounds = isSystem;
        Key = isSystem ? AudioProfiles.SystemKey : AudioProfiles.KeyFor(executablePath);
    }

    /// <summary>The service's key: executable path, "system" or "pid:N".</summary>
    internal string Id { get; }

    public string Key { get; }
    public string? ExecutablePath { get; }
    public string AppName { get; }
    public bool IsSystemSounds { get; }

    /// <summary>A source card shows this app's sound controls (keeps the sessions watched).</summary>
    internal bool IsTracked { get; set; }

    /// <summary>Process whose tree is captured for listening (the window's process; child processes included).</summary>
    public uint ProcessId { get; internal set; }

    public bool HasSessions => _sessions.Count > 0;

    public int SessionCount => _sessions.Count;

    public bool IsPlayingSound => _sessions.Any(s => AppAudioService.Read(() => s.State, AudioSessionState.AudioSessionStateExpired) == AudioSessionState.AudioSessionStateActive);

    /// <summary>0..1, the loudest of the app's sessions (before its volume is applied); rises at once, falls back smoothly.</summary>
    public float Peak { get; private set; }

    public float LevelNow { get; private set; }

    public float Volume
    {
        get => _sessions.Count > 0 ? AppAudioService.Read(() => _sessions[0].SimpleAudioVolume.Volume, 1f) : 1f;
        set
        {
            var volume = Math.Clamp(value, 0f, 1f);
            foreach (var session in _sessions)
            {
                AppAudioService.Read(() => session.SimpleAudioVolume.Volume = volume, 0f);
            }

            _lastVolume = volume; // our own change: not reported back as an outside change
        }
    }

    public bool Muted
    {
        get => _sessions.Count > 0 && AppAudioService.Read(() => _sessions[0].SimpleAudioVolume.Mute, false);
        set
        {
            foreach (var session in _sessions)
            {
                AppAudioService.Read(() => session.SimpleAudioVolume.Mute = value, false);
            }

            _lastMuted = value;
        }
    }

    public bool IsListening => _recorder is not null;

    public string? ListeningDeviceName { get; private set; }

    /// <summary>Raised (UI thread) when sessions, volume, mute, level (cards) or listening state change.</summary>
    public event EventHandler? Changed;

    public event EventHandler? PeakChanged;

    internal void SetSessions(List<AudioSessionControl> sessions)
    {
        var changed = sessions.Count != _sessions.Count || !sessions.SequenceEqual(_sessions);
        _sessions = sessions;
        if (sessions.Count == 0)
        {
            if (Peak != 0)
            {
                Peak = 0;
                PeakChanged?.Invoke(this, EventArgs.Empty);
            }

            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        // Volume, mute or playing state changed outside VIZEN (Windows volume mixer, the app itself)?
        var volume = Volume;
        var muted = Muted;
        var active = IsPlayingSound ? 1 : 0;
        if (Math.Abs(volume - _lastVolume) > 0.004f || muted != _lastMuted || active != _lastActive)
        {
            changed = true;
        }

        (_lastVolume, _lastMuted, _lastActive) = (volume, muted, active);
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void UpdatePeak()
    {
        var peak = 0f;
        foreach (var session in _sessions)
        {
            peak = Math.Max(peak, AppAudioService.Read(() => session.AudioMeterInformation.MasterPeakValue, 0f));
        }

        LevelNow = peak;

        // Rises at once, falls back smoothly (like a hardware meter): ~0.5 s from full to zero at 70 ms per tick.
        // The meter keeps changing only while it falls, then it is quiet again.
        var shown = Math.Max(peak, Peak * 0.72f);
        if (shown < 0.01f)
        {
            shown = 0;
        }

        if (Math.Abs(shown - Peak) > 0.003f || (shown == 0 && Peak != 0))
        {
            Peak = shown;
            PeakChanged?.Invoke(this, EventArgs.Empty);
            if (IsTracked)
            {
                Changed?.Invoke(this, EventArgs.Empty); // the source cards read the level from Changed
            }
        }
    }

    /// <summary>
    /// Plays only this app's sound on <paramref name="deviceId"/> (null = default output). Use a different device
    /// than the one the app already plays on (e.g. headphones), otherwise you hear it twice.
    /// Returns an error message, or null on success.
    /// </summary>
    public async Task<string?> StartListeningAsync(string? deviceId)
    {
        StopListening();
        if (!AppAudioService.IsListenSupported)
        {
            return "Windows 10 2004+";
        }

        if (_service.Device(deviceId) is not { } device)
        {
            return "device not found";
        }

        try
        {
            var recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback(ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
                .BuildAsync();
            var buffer = new BufferedWaveProvider(recorder.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = true };
            recorder.DataAvailable += (data, _, _, _) =>
            {
                var bytes = data.ToArray();
                buffer.AddSamples(bytes, 0, bytes.Length);
            };
            var player = await new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithLatency(80).BuildAsync();
            player.Init(buffer);
            recorder.StartRecording();
            player.Play();
            (_recorder, _player, _buffer) = (recorder, player, buffer);
            ListeningDeviceName = DemoMode.IsOn ? "Output device" : device.FriendlyName;
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            StopListening();
            return ex.Message;
        }
    }

    public void StopListening()
    {
        var wasListening = _recorder is not null;
        try
        {
            _recorder?.StopRecording();
            _player?.Stop();
        }
        catch (COMException)
        {
            // Device already gone.
        }

        _recorder?.Dispose();
        _player?.Dispose();
        (_recorder, _player, _buffer) = (null, null, null);
        ListeningDeviceName = null;
        if (wasListening)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
