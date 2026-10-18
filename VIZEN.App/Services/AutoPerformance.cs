using System.Collections.ObjectModel;
using System.Windows.Threading;
using VIZEN.App.Capture;
using VIZEN.App.Localization;

namespace VIZEN.App.Services;

/// <summary>
/// Auto Performance: watches every capture once a second. When VIZEN itself drops frames for a few seconds it
/// names the likely bottleneck and tells the user. Only when the user allowed it does it lower that capture to
/// 30 fps; it then tries the full frame rate again later and reports every step. It never touches captures that
/// are limited by their source (a 30 fps video stays 30 fps) and never changes resolution or quality.
/// </summary>
public sealed class AutoPerformanceService : IDisposable
{
    public const double ReducedFps = 30;
    private const int TriggerSeconds = 3;
    private const int MaxLogEntries = 40;
    private static readonly TimeSpan NotifyCooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProbeLength = TimeSpan.FromSeconds(10);

    private readonly CaptureService _capture;
    private readonly PerformanceMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly ActivityLog _activity;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<LiveCapture, State> _states = [];

    public AutoPerformanceService(CaptureService capture, PerformanceMonitor monitor, AppSettings settings, ActivityLog activity)
    {
        _capture = capture;
        _monitor = monitor;
        _settings = settings;
        _activity = activity;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.AutoPerformance) or nameof(AppSettings.AllowFpsReduction)
                && !(_settings.AutoPerformance && _settings.AllowFpsReduction))
            {
                RemoveAllCaps();
            }
        };
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    /// <summary>What Auto Performance noticed and did in this session (newest first).</summary>
    public ObservableCollection<ActivityEntry> Log { get; } = [];

    /// <summary>Removes every Auto Performance limit now (the "full frame rate" button).</summary>
    public void RemoveAllCaps()
    {
        foreach (var active in _capture.ActiveCaptures.Where(a => a.Capture.AutoFpsCap > 0))
        {
            active.Capture.AutoFpsCap = 0;
            AddLog("", "Perf.Log.Restored", NameOf(active));
        }

        _states.Clear();
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var active = _capture.ActiveCaptures;
        foreach (var gone in _states.Keys.Where(k => active.All(a => !ReferenceEquals(a.Capture, k))).ToList())
        {
            _states.Remove(gone);
        }

        if (!_settings.AutoPerformance)
        {
            return;
        }

        foreach (var item in active)
        {
            var capture = item.Capture;
            var stats = capture.Stats;
            if (!_states.TryGetValue(capture, out var state))
            {
                state = new State();
                _states[capture] = state;
            }

            DetectStall(item, stats, state, now);

            state.LimitedSeconds = CaptureStats.IsVizenLimited(stats.Health) ? state.LimitedSeconds + 1 : 0;
            if (state.LimitedSeconds >= TriggerSeconds)
            {
                state.LimitedSeconds = 0;
                OnVizenLimited(item, stats, state, now);
            }
            else if (capture.AutoFpsCap > 0)
            {
                TryRestore(item, state, now);
            }
        }
    }

    private void DetectStall(ActiveCapture item, CaptureStats stats, State state, DateTime now)
    {
        if (stats.SourceFps >= 20)
        {
            state.LastBusyAt = now;
        }

        // A smooth source that suddenly sends nothing: frozen app, paused video or a hung window. Logged, not
        // toasted, because a paused video looks exactly the same to Windows.
        if (stats.Health == CaptureHealth.NoNewFrames && state.LastHealth != CaptureHealth.NoNewFrames
            && now - state.LastBusyAt < TimeSpan.FromSeconds(5))
        {
            AddLog("", "Perf.Log.Stalled", NameOf(item));
        }

        state.LastHealth = stats.Health;
    }

    private void OnVizenLimited(ActiveCapture item, CaptureStats stats, State state, DateTime now)
    {
        var capture = item.Capture;
        var cause = Loc.T(Diagnose(stats, _monitor.Latest));
        if (state.ProbeStartedAt is not null)
        {
            // The full frame rate was tried again and VIZEN still cannot keep up: back to 30 fps, wait longer.
            capture.AutoFpsCap = ReducedFps;
            state.ProbeStartedAt = null;
            state.CappedAt = now;
            state.RetryAfter = TimeSpan.FromTicks(Math.Min(state.RetryAfter.Ticks * 2, MaxRetry.Ticks));
            AddLog("", "Perf.Log.StillLimited", NameOf(item), cause, (int)state.RetryAfter.TotalSeconds);
            return;
        }

        var canReduce = _settings.AllowFpsReduction && capture.AutoFpsCap <= 0
            && (capture.RequestedFps <= 0 || capture.RequestedFps > ReducedFps) && stats.SourceFps > ReducedFps + 1;
        if (canReduce)
        {
            capture.AutoFpsCap = ReducedFps;
            state.CappedAt = now;
            state.RetryAfter = FirstRetry;
            AddLog("", "Perf.Log.Reduced", NameOf(item), cause, stats.SourceFps.ToString("0"), stats.DroppedPercent.ToString("0.#"));
            Notify(state, now, "Perf.Toast.Reduced", NameOf(item), cause);
            return;
        }

        AddLog("", "Perf.Log.Limited", NameOf(item), cause, stats.DroppedPercent.ToString("0.#"));
        Notify(state, now, _settings.AllowFpsReduction ? "Perf.Toast.Limited" : "Perf.Toast.LimitedAsk", NameOf(item), cause);
    }

    private void TryRestore(ActiveCapture item, State state, DateTime now)
    {
        if (state.ProbeStartedAt is { } probe)
        {
            if (now - probe >= ProbeLength)
            {
                state.ProbeStartedAt = null;
                state.RetryAfter = FirstRetry;
                AddLog("", "Perf.Log.Restored", NameOf(item));
                Notify(state, now, "Perf.Toast.Restored", NameOf(item), "");
            }

            return;
        }

        if (now - state.CappedAt >= state.RetryAfter)
        {
            // Try the full frame rate; OnVizenLimited puts the cap back if frames drop again.
            item.Capture.AutoFpsCap = 0;
            state.ProbeStartedAt = now;
            AddLog("", "Perf.Log.Probing", NameOf(item));
        }
    }

    /// <summary>Resource key of the most likely reason VIZEN drops frames, from measured values only.</summary>
    internal static string Diagnose(CaptureStats stats, ResourceSnapshot resources)
    {
        if (resources.GpuTotalPercent >= 90)
        {
            return "Perf.Cause.Gpu";
        }

        if (resources.SystemCpuPercent >= 90)
        {
            return "Perf.Cause.Cpu";
        }

        return stats.Health switch
        {
            CaptureHealth.CaptureLimited => "Perf.Cause.Capture",
            CaptureHealth.ProcessingLimited => "Perf.Cause.Copy",
            CaptureHealth.RenderLimited => "Perf.Cause.Present",
            CaptureHealth.PresentationLimited => "Perf.Cause.Presentation",
            _ when stats.CopyMs > 8 => "Perf.Cause.Copy",
            _ when stats.PresentMs > 8 => "Perf.Cause.Present",
            _ => "Perf.Cause.Busy",
        };
    }

    private void Notify(State state, DateTime now, string detailKey, params object?[] args)
    {
        if (now - state.LastNotifiedAt < NotifyCooldown)
        {
            return;
        }

        state.LastNotifiedAt = now;
        _activity.Add("", "Perf.Toast.Title", detailKey, args);
    }

    private void AddLog(string glyph, string detailKey, params object?[] args)
    {
        Log.Insert(0, new ActivityEntry(DateTime.Now, glyph, "Perf.Toast.Title", detailKey, args));
        while (Log.Count > MaxLogEntries)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }

    private static string NameOf(ActiveCapture item)
    {
        var title = item.Source.Title;
        return title.Length > 48 ? title[..47] + "…" : title;
    }

    public void Dispose() => _timer.Stop();

    private sealed class State
    {
        public int LimitedSeconds;
        public CaptureHealth LastHealth;
        public DateTime LastBusyAt = DateTime.MinValue;
        public DateTime LastNotifiedAt = DateTime.MinValue;
        public DateTime CappedAt;
        public DateTime? ProbeStartedAt;
        public TimeSpan RetryAfter = FirstRetry;
    }
}
