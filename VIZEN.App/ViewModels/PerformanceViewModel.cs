using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using VIZEN.App.Capture;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>One running capture on the Performance page; refreshes with the capture's per-second measurements.</summary>
public sealed class CaptureRowViewModel : ObservableObject
{
    private readonly LiveCapture _capture;

    public CaptureRowViewModel(ActiveCapture active)
    {
        _capture = active.Capture;
        Name = active.Source.Title;
        App = active.Source.ProcessName;
        Consumers = active.Consumers;
        _capture.PropertyChanged += OnCaptureChanged;
    }

    public string Name { get; }

    public string App { get; }

    public int Consumers { get; }

    private CaptureStats S => _capture.Stats;

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public string HealthText => _capture.HealthText;

    public CaptureHealth Health => S.Health;

    public string ConsumersText => Loc.F("Perf.Consumers", Consumers);

    public string SizeText => S.Width > 0 ? $"{S.Width}×{S.Height}" : "—";

    public string SourceFpsText => S.SourceFps.ToString("0.#", Culture);

    public string PresentedFpsText => S.PresentedFps.ToString("0.#", Culture);

    public string DroppedText => S.DroppedPercent.ToString("0.#", Culture) + "%";

    public string LatencyText => S.LatencyMs is { } avg
        ? Loc.F("Perf.LatencyAvgMax", avg.ToString("0.0", Culture), (S.LatencyMaxMs ?? avg).ToString("0", Culture))
        : "—";

    public string JitterText => S.PresentedFps > 1 ? S.JitterMs.ToString("0.0", Culture) + " ms" : "—";

    public string WorkText => Loc.F("Perf.Work", S.CopyMs.ToString("0.00", Culture), S.PresentMs.ToString("0.00", Culture));

    /// <summary>How much of the time the capture thread was busy, and the GPU-wait part per frame.</summary>
    public string CaptureBusyText => Loc.F("Perf.CaptureBusy", S.CaptureBusyPercent.ToString("0", Culture), S.GpuWaitMs.ToString("0.00", Culture));

    public string UiDelayText => S.PresentedFps > 0 ? S.UiDelayMs.ToString("0.0", Culture) + " ms" : "—";

    public string DisplayLoopText => S.RenderLoopFps > 0
        ? Loc.F("Perf.DisplayLoop", S.RenderLoopFps.ToString("0", Culture), S.DisplayHz.ToString("0", Culture))
        : "—";

    /// <summary>Exact skipped/repeated frames for VIZEN test sources (read from the frame marker).</summary>
    public string MarkerText => S.SkippedFrames is { } skipped
        ? Loc.F("Perf.Marker", skipped, S.RepeatedFrames ?? 0)
        : Loc.T("Perf.MarkerNone");

    public bool HasMarker => _capture.HasFrameMarker;

    public string PipelineText => _capture.PipelineDescription;

    public string CapText => _capture.IsAutoCapped
        ? Loc.F("Perf.CapAuto", _capture.MaxFps.ToString("0", Culture))
        : _capture.MaxFps > 0 ? Loc.F("Perf.CapConsumer", _capture.MaxFps.ToString("0", Culture)) : Loc.T("Perf.CapNone");

    public bool IsAutoCapped => _capture.IsAutoCapped;

    private void OnCaptureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LiveCapture.Stats) or nameof(LiveCapture.AutoFpsCap) or nameof(LiveCapture.MaxFps))
        {
            OnPropertyChanged(string.Empty);
        }
    }

    public void Detach() => _capture.PropertyChanged -= OnCaptureChanged;
}

/// <summary>
/// Performance page: live resource use, every capture's measurements, Auto Performance and what this PC can do.
/// Everything shown is measured or read from Windows; the tier is labelled as an estimate.
/// </summary>
public sealed class PerformanceViewModel : ObservableObject
{
    private readonly CaptureService _capture;
    private readonly PerformanceMonitor _monitor;
    private SystemInfo? _system;
    private bool _detecting;

    private readonly OverlaysViewModel _overlays;
    private readonly ActivityLog _activity;
    private readonly Action<ObservableObject> _navigate;
    private int _testCount;

    public PerformanceViewModel(CaptureService capture, PerformanceMonitor monitor, AutoPerformanceService auto, AppSettings settings,
        OverlaysViewModel overlays, ActivityLog activity, Action<ObservableObject> navigate)
    {
        _capture = capture;
        _monitor = monitor;
        Auto = auto;
        Settings = settings;
        _overlays = overlays;
        _activity = activity;
        _navigate = navigate;
        OpenTestPatternCommand = new RelayCommand<string>(fps => OpenTestPattern(ParseFps(fps)));
        StartSyntheticCommand = new RelayCommand<string>(fps => StartSynthetic(ParseFps(fps)));
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        _monitor.Sampled += (_, _) => OnPropertyChanged(nameof(Resources));
        _capture.CapturesChanged += (_, _) => RebuildCaptures();
        RemoveCapsCommand = new RelayCommand(Auto.RemoveAllCaps);
        RefreshSystemCommand = new RelayCommand(() => _ = DetectAsync(), () => !_detecting);
        RebuildCaptures();
    }

    public AppSettings Settings { get; }

    public AutoPerformanceService Auto { get; }

    public ObservableCollection<CaptureRowViewModel> Captures { get; } = [];

    public bool HasCaptures => Captures.Count > 0;

    public ICommand RemoveCapsCommand { get; }

    public ICommand RefreshSystemCommand { get; }

    /// <summary>Starts a test-pattern window (separate process, captured like any app). Parameter: "30" or "60".</summary>
    public ICommand OpenTestPatternCommand { get; }

    /// <summary>Opens an overlay of a synthetic source (no Windows capture). Parameter: "30" or "60".</summary>
    public ICommand StartSyntheticCommand { get; }

    public ICommand ExportDiagnosticsCommand { get; }

    private static int ParseFps(string? value) => int.TryParse(value, out var fps) ? fps : 60;

    private void OpenTestPattern(int fps)
    {
        var id = ++_testCount;
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe,
            $"{Views.TestPatternWindow.Argument} {fps} {id} 1920x1080") { UseShellExecute = false });
        _activity.Add("\uE7F4", "Perf.Test.PatternOpened", "Perf.Test.PatternOpenedDetail", id, fps);
    }

    private void StartSynthetic(int fps)
    {
        var id = ++_testCount;
        var source = new Models.WindowSource
        {
            Handle = IntPtr.Zero,
            ProcessName = "VIZEN",
            ProcessId = (uint)Environment.ProcessId,
            ClassName = $"synthetic-{id}",
            SyntheticFps = fps,
        };
        source.Title = Loc.F("Perf.Test.SyntheticTitle", id, fps);
        source.Width = 1920;
        source.Height = 1080;
        if (_overlays.CreateOverlay(source))
        {
            _navigate(_overlays);
        }
    }

    private void ExportDiagnostics()
    {
        try
        {
            var path = DiagnosticsExport.Write(_capture, _monitor, Auto, Settings);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            _activity.Add("\uE896", "Perf.Export.Done", "Perf.Export.DoneDetail", path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _activity.Add("\uE7BA", "Perf.Export.Failed", ActivityLog.RawDetail, ex.Message);
        }
    }

    /// <summary>Bound as one object so the resource tiles refresh together once a second.</summary>
    public ResourceTexts Resources => new(_monitor.Latest, _monitor.GpuUnavailable, _monitor.GpuMemoryUnavailable, _system?.MemoryGb);

    public bool IsSystemKnown => _system is not null;

    public string CpuText => _system is null ? "…" : Loc.F("Perf.Sys.Cpu", _system.CpuName, _system.LogicalProcessors);

    public string MemoryText => _system is null ? "…" : Loc.F("Perf.Sys.Memory", _system.MemoryGb.ToString("0.0", CultureInfo.CurrentCulture));

    public IReadOnlyList<string> GpuLines => _system is null ? [] : _system.Gpus.Count == 0
        ? [Loc.T("Perf.Sys.NoGpu")]
        : _system.Gpus.Select(g => Loc.F(g.IsLikelyIntegrated ? "Perf.Sys.GpuIntegrated" : "Perf.Sys.GpuDedicated", g.Name,
            g.DedicatedMemoryGb.ToString("0.#", CultureInfo.CurrentCulture), g.SharedMemoryGb.ToString("0.#", CultureInfo.CurrentCulture))).ToList();

    public IReadOnlyList<string> DisplayLines => _system is null ? [] : _system.Displays
        .Select(d => Loc.F(d.IsPrimary ? "Perf.Sys.DisplayPrimary" : "Perf.Sys.Display", d.Width, d.Height, d.RefreshHz)).ToList();

    public string WindowsText => _system?.WindowsVersion ?? "…";

    public IReadOnlyList<FeatureLine> Features => _system is null ? [] :
    [
        new(Loc.T("Perf.Feat.Capture"), _system.WindowCaptureSupported),
        new(Loc.T("Perf.Feat.Borderless"), _system.BorderlessCaptureSupported),
        new(Loc.T("Perf.Feat.AppAudio"), _system.AppAudioSupported),
        new(_system.WebView2Version is { } v ? Loc.F("Perf.Feat.WebView2", v) : Loc.T("Perf.Feat.WebView2Missing"), _system.WebView2Version is not null),
    ];

    public PerformanceTier? Tier => _system is null ? null : SystemCompatibility.EstimateTier(_system);

    public string TierName => Tier is { } t ? Loc.T("Tier." + t) : "…";

    public string TierDescription => Tier is { } t ? Loc.T("Tier." + t + ".Description") : "";

    public IReadOnlyList<string> Recommendations => _system is null ? [] :
        SystemCompatibility.Recommendations(_system).Select(r => Loc.F(r.Key, r.Args)).ToList();

    /// <summary>Reads the hardware the first time the page is shown (a few milliseconds, off the UI thread).</summary>
    public void EnsureSystemDetected()
    {
        if (_system is null && !_detecting)
        {
            _ = DetectAsync();
        }
    }

    private async Task DetectAsync()
    {
        _detecting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var supported = _capture.IsSupported;
            _system = await Task.Run(() => SystemCompatibility.Detect(supported));
        }
        finally
        {
            _detecting = false;
            CommandManager.InvalidateRequerySuggested();
        }

        OnPropertyChanged(string.Empty);
    }

    private void RebuildCaptures()
    {
        foreach (var row in Captures)
        {
            row.Detach();
        }

        Captures.Clear();
        foreach (var active in _capture.ActiveCaptures)
        {
            Captures.Add(new CaptureRowViewModel(active));
        }

        OnPropertyChanged(nameof(HasCaptures));
    }
}

public sealed record FeatureLine(string Name, bool IsAvailable);

/// <summary>How heavy a measured value is, in plain words, by the guide limits in <see cref="ResourceTexts"/>.</summary>
public enum ResourceLevel
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>
/// Resource tiles in plain words: the measured value, a level (low / medium / high) and what it means. The levels are a
/// guide with fixed limits, stated on the page; the numbers themselves are measured. GPU values read "unavailable" when
/// Windows has no GPU counters.
/// </summary>
/// <param name="gpuUnavailable">The GPU counters do not exist here; otherwise a missing value means the first sample is still being taken.</param>
/// <param name="totalMemoryGb">The PC's memory, or null while unknown.</param>
public sealed class ResourceTexts(ResourceSnapshot r, bool gpuUnavailable = false, bool gpuMemoryUnavailable = false, double? totalMemoryGb = null)
{
    // Guide limits (percent): below the first = low, below the second = medium, otherwise high.
    public const double VizenCpuMedium = 5, VizenCpuHigh = 15;
    public const double SystemCpuMedium = 60, SystemCpuHigh = 85;
    public const double GpuMedium = 70, GpuHigh = 90;
    public const double MemoryShareMedium = 5, MemoryShareHigh = 15;

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public string VizenCpu => r.VizenCpuPercent.ToString("0.0", Culture) + "%";

    public ResourceLevel VizenCpuLevel => Level(r.VizenCpuPercent, VizenCpuMedium, VizenCpuHigh);

    public string SystemCpu => r.SystemCpuPercent.ToString("0", Culture) + "%";

    public ResourceLevel SystemCpuLevel => Level(r.SystemCpuPercent, SystemCpuMedium, SystemCpuHigh);

    public string Gpu => r.GpuTotalPercent is { } total ? total.ToString("0", Culture) + "%" : Loc.T(gpuUnavailable ? "Perf.Unavailable" : "Perf.Measuring");

    public ResourceLevel GpuLevel => r.GpuTotalPercent is { } total ? Level(total, GpuMedium, GpuHigh) : ResourceLevel.None;

    public string VizenGpu => r.VizenGpuPercent is { } own ? Loc.F("Perf.VizenGpu", own.ToString("0.0", Culture)) : "";

    /// <summary>RAM VIZEN uses (working set, as Task Manager's "Memory" column shows it).</summary>
    public string Memory => r.WorkingSetMb.ToString("0", Culture) + " MB";

    public string MemoryShare => totalMemoryGb is > 0 and var gb
        ? Loc.F("Perf.MemoryShare", (r.WorkingSetMb / 1024 / gb * 100).ToString("0.0", Culture), gb.ToString("0", Culture))
        : "";

    public ResourceLevel MemoryLevel => totalMemoryGb is > 0 and var gb && r.WorkingSetMb > 0
        ? Level(r.WorkingSetMb / 1024 / gb * 100, MemoryShareMedium, MemoryShareHigh)
        : ResourceLevel.None;

    /// <summary>Technical detail for the tooltip: private memory.</summary>
    public string MemoryDetail => Loc.F("Perf.MemoryValue", r.WorkingSetMb.ToString("0", Culture), r.PrivateMb.ToString("0", Culture));

    public string GpuMemory => r.VizenGpuDedicatedMb is { } d
        ? Loc.F("Perf.GpuMemory", d.ToString("0", Culture), (r.VizenGpuSharedMb ?? 0).ToString("0", Culture))
        : Loc.T(gpuMemoryUnavailable ? "Perf.Unavailable" : "Perf.Measuring");

    public string VizenCpuLevelText => LevelText(VizenCpuLevel);

    public string SystemCpuLevelText => LevelText(SystemCpuLevel);

    public string GpuLevelText => LevelText(GpuLevel);

    public string MemoryLevelText => LevelText(MemoryLevel);

    public static string LevelText(ResourceLevel level) => level == ResourceLevel.None ? "" : Loc.T("Perf.Level." + level);

    public static ResourceLevel Level(double value, double medium, double high) =>
        value < medium ? ResourceLevel.Low : value < high ? ResourceLevel.Medium : ResourceLevel.High;
}
