using System.Runtime.InteropServices;
using VIZEN.App.Models;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace VIZEN.App.Capture;

/// <summary>
/// A consumer's hold on a shared <see cref="LiveCapture"/>. Dispose when done; the capture stops
/// when its last lease is released.
/// </summary>
public sealed class CaptureLease : IDisposable
{
    private readonly CaptureService _owner;
    private bool _disposed;

    internal CaptureLease(CaptureService owner, LiveCapture capture, double maxFps)
    {
        _owner = owner;
        Capture = capture;
        MaxFps = maxFps;
    }

    public LiveCapture Capture { get; }

    /// <summary>Frame-rate cap this consumer needs; 0 = as fast as the source updates.</summary>
    public double MaxFps { get; }

    /// <summary>True while this consumer shows the capture through its own native presenter (no CPU copy needed).</summary>
    public bool UsesNativePresenter { get; private set; }

    internal bool InitialNative { init => UsesNativePresenter = value; }

    /// <summary>UI thread: tells the service whether this consumer still needs CPU copies of the frames.</summary>
    public void SetNativePresenter(bool value)
    {
        if (UsesNativePresenter != value && !_disposed)
        {
            UsesNativePresenter = value;
            _owner.Reapply(this);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.Release(this);
    }
}

/// <summary>One running capture as listed by <see cref="CaptureService.ActiveCaptures"/>.</summary>
public sealed record ActiveCapture(LiveCapture Capture, WindowSource Source, int Consumers);

/// <summary>
/// Entry point for live capture of windows (Windows Graphics Capture) and cameras (MediaCapture).
/// Shares one capture per source between all consumers (preview, region editor, overlays, OBS outputs),
/// so cropping several regions out of one source costs a single capture. UI thread only.
/// </summary>
public sealed class CaptureService : IDisposable
{
    private readonly Dictionary<object, SharedCapture> _shared = [];
    private readonly ISourceWindowHost? _windowHost;
    private bool _borderlessAllowed;
    private bool _recovering;

    public CaptureService(ISourceWindowHost? windowHost = null)
    {
        _windowHost = windowHost;
        try
        {
            IsSupported = GraphicsCaptureSession.IsSupported();
        }
        catch (Exception)
        {
            IsSupported = false;
        }
    }

    /// <summary>
    /// Show window captures through the GPU (shared texture → D3DImage) instead of copying every frame through
    /// system memory. Applies to captures started afterwards; each capture falls back to the CPU path on its own.
    /// </summary>
    public bool GpuRendering { get; set; } = true;

    /// <summary>True when this version of Windows supports window capture (Windows.Graphics.Capture).</summary>
    public bool IsSupported { get; }

    /// <summary>Number of sources currently being captured (for diagnostics/tests).</summary>
    public int ActiveCaptureCount => _shared.Count;

    /// <summary>Every running capture with its source and how many consumers share it (Performance page, Auto Performance).</summary>
    public IReadOnlyList<ActiveCapture> ActiveCaptures =>
        _shared.Values.Select(s => new ActiveCapture(s.Capture, s.Source, s.Leases.Count)).ToList();

    /// <summary>A capture started or stopped, or its consumers changed. UI thread.</summary>
    public event EventHandler? CapturesChanged;

    /// <summary>
    /// On Windows 11 asks for permission to capture windows without the yellow capture border.
    /// Capture works either way; this is cosmetic.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!IsSupported || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
            || !ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
        {
            return;
        }

        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            _borderlessAllowed = status == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception)
        {
            _borderlessAllowed = false;
        }
    }

    /// <summary>True if <paramref name="source"/> can be captured on this system.</summary>
    public bool CanCapture(WindowSource source) => source.IsCamera || source.IsSynthetic || IsSupported;

    public static object KeyFor(WindowSource source) =>
        source.CameraDeviceId is { } id ? "camera:" + id
        : source.IsSynthetic ? "synthetic:" + source.ClassName
        : source.Handle;

    /// <summary>
    /// Returns a lease on the live capture of <paramref name="source"/>, starting it if needed.
    /// Never throws for capture failures: inspect <see cref="LiveCapture.Status"/> instead (camera start-up
    /// is asynchronous, so a camera may move to Failed shortly after this returns).
    /// </summary>
    /// <param name="maxFps">Frame-rate cap this consumer needs; 0 = as fast as the source updates.</param>
    /// <param name="presentsNatively">
    /// The consumer will show the capture through a native presenter: no CPU copies are made for it from the start
    /// (it calls <see cref="CaptureLease.SetNativePresenter"/>(false) if that does not work out).
    /// </param>
    public CaptureLease Acquire(WindowSource source, double maxFps = 0, bool presentsNatively = false)
    {
        if (!CanCapture(source))
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture is not supported on this system.");
        }

        var key = KeyFor(source);
        if (!_shared.TryGetValue(key, out var shared) || !shared.Capture.IsUsable)
        {
            // Nothing running for this source, or the previous capture ended: start a fresh one.
            // Any remaining leases on the dead capture keep it until they are released.
            shared = new SharedCapture(Start(source, maxFps), source);
            _shared[key] = shared;
        }

        var lease = new CaptureLease(this, shared.Capture, maxFps)
        {
            InitialNative = presentsNatively && shared.Capture is WindowCapture { SupportsNativePresentation: true },
        };
        shared.Leases.Add(lease);
        ApplyFrameRate(shared);
        CapturesChanged?.Invoke(this, EventArgs.Empty);
        return lease;
    }

    private LiveCapture Start(WindowSource source, double maxFps)
    {
        if (source.SyntheticFps is { } fps)
        {
            return new SyntheticCapture(KeyFor(source), Math.Max(64, source.Width), Math.Max(64, source.Height), fps);
        }

        if (source.CameraDeviceId is { } deviceId)
        {
            var camera = new CameraCapture(deviceId, maxFps);
            _ = camera.StartAsync();
            return camera;
        }

        // Normally one Direct3D device per window capture, so sources never wait for each other (see PerCaptureDevice).
        var window = new WindowCapture(AcquireDevice(), source.Handle, maxFps, _borderlessAllowed,
            _windowHost, source.ExecutablePath ?? source.ProcessName, source.Title, GpuRendering);
        window.DeviceLost += OnDeviceLost;
        window.Start();
        return window;
    }

    internal void Release(CaptureLease lease)
    {
        var shared = _shared.Values.FirstOrDefault(s => ReferenceEquals(s.Capture, lease.Capture));
        if (shared is null)
        {
            // The capture was replaced after it ended; this lease was its last holder.
            lease.Capture.Dispose();
            return;
        }

        shared.Leases.Remove(lease);
        if (shared.Leases.Count == 0)
        {
            shared.Capture.Dispose();
            _shared.Remove(shared.Capture.Key);
        }
        else
        {
            ApplyFrameRate(shared);
        }

        CapturesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>CPU-copy rate for in-app thumbnails when every consumer of a capture presents natively.</summary>
    public const double ThumbnailFps = 5;

    private bool _thumbnailsVisible;

    /// <summary>
    /// True while a page shows the overlay thumbnails (Gaming). Captures shown only natively then refresh their WPF
    /// image at <see cref="ThumbnailFps"/>; otherwise not at all (no CPU copy, no WPF image updates).
    /// </summary>
    public bool ThumbnailsVisible
    {
        get => _thumbnailsVisible;
        set
        {
            if (_thumbnailsVisible != value)
            {
                _thumbnailsVisible = value;
                foreach (var shared in _shared.Values)
                {
                    ApplyFrameRate(shared, value);
                }
            }
        }
    }

    internal void Reapply(CaptureLease lease)
    {
        if (_shared.Values.FirstOrDefault(s => ReferenceEquals(s.Capture, lease.Capture)) is { } shared)
        {
            ApplyFrameRate(shared, _thumbnailsVisible);
        }
    }

    private void ApplyFrameRate(SharedCapture shared) => ApplyFrameRate(shared, _thumbnailsVisible);

    private static void ApplyFrameRate(SharedCapture shared, bool thumbnailsVisible)
    {
        // The most demanding consumer wins; 0 (uncapped) beats any cap.
        shared.Capture.RequestedFps = shared.Leases.Any(l => l.MaxFps <= 0) ? 0 : shared.Leases.Max(l => l.MaxFps);

        // CPU copies are only for consumers that show a WPF image; native presenters don't need them.
        var cpuLeases = shared.Leases.Where(l => !l.UsesNativePresenter).ToList();
        shared.Capture.CpuFrameMaxFps = cpuLeases.Count == 0 ? (thumbnailsVisible ? ThumbnailFps : -1)
            : cpuLeases.Any(l => l.MaxFps <= 0) ? 0
            : cpuLeases.Max(l => l.MaxFps);
    }

    private CaptureDevice? _device;

    /// <summary>
    /// One device per capture (default). Measured on an integrated GPU with 4 × 1080p60: a shared device used ~85 MB less RAM but
    /// showed ~7 % fewer frames (the capture threads take turns on one device lock), and fidelity comes first.
    /// VIZEN_DEVICE=shared selects the shared device for comparison.
    /// </summary>
    private static readonly bool PerCaptureDevice = Environment.GetEnvironmentVariable("VIZEN_DEVICE") != "shared";

    /// <summary>A reference to the shared device for a new capture (created on first use).</summary>
    private CaptureDevice AcquireDevice()
    {
        if (PerCaptureDevice)
        {
            return CaptureDevice.Create();
        }

        if (_device is null)
        {
            MemTrace.Mark("capture: before device");
            _device = CaptureDevice.Create();
            MemTrace.Mark("capture: device created", _device.Device);
        }

        return _device.AddRef();
    }

    /// <summary>After a device loss: the new shared device (created once), with a reference for the caller.</summary>
    private CaptureDevice ReplaceDevice(CaptureDevice lost)
    {
        if (ReferenceEquals(_device, lost) || _device is null)
        {
            var old = _device;
            _device = CaptureDevice.Create();
            try
            {
                old?.Dispose(); // the service's own reference
            }
            catch (COMException)
            {
                // Already unusable.
            }
        }

        return _device.AddRef();
    }

    /// <summary>Number of times capture recovered from a lost GPU device in this session.</summary>
    public int DeviceRecoveries { get; private set; }

    /// <summary>
    /// The GPU device was lost (driver update or crash, GPU reset — this affects every device on the adapter).
    /// Restarts every window capture on a new device; previews, overlays, outputs and compositions keep their leases
    /// and continue.
    /// </summary>
    private void OnDeviceLost(object? sender, CaptureDevice lost)
    {
        // Every capture on the lost device reports it: the first report moves them all, later ones are stale.
        if (_recovering || (!PerCaptureDevice && !ReferenceEquals(lost, _device)))
        {
            return;
        }

        _recovering = true;
        var windows = _shared.Values.Select(s => s.Capture).OfType<WindowCapture>()
            .Where(c => c.IsUsable && (!PerCaptureDevice || ReferenceEquals(c.Device, lost))).ToList();
        try
        {
            foreach (var capture in windows)
            {
                var old = capture.Device;
                capture.Rebind(PerCaptureDevice ? CaptureDevice.Create() : ReplaceDevice(lost));
                try
                {
                    old.Dispose();
                }
                catch (COMException)
                {
                    // The old device is already unusable.
                }
            }

            DeviceRecoveries++;
            _windowHost?.ReportDeviceRecovered(windows.Count, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            _windowHost?.ReportDeviceRecovered(windows.Count, ex.Message);
        }
        finally
        {
            _recovering = false;
        }
    }

    public void Dispose()
    {
        foreach (var shared in _shared.Values)
        {
            shared.Capture.Dispose();
        }

        _shared.Clear();
        _device?.Dispose();
        _device = null;
    }

    private sealed class SharedCapture(LiveCapture capture, WindowSource source)
    {
        public LiveCapture Capture { get; } = capture;
        public WindowSource Source { get; } = source;
        public List<CaptureLease> Leases { get; } = [];
    }
}
