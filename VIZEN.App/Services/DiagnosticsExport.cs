using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using VIZEN.App.Capture;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Services;

/// <summary>
/// Writes everything needed to analyse a performance problem on another PC into one JSON file: this PC, the
/// display, every running capture with its last five minutes of per-second measurements, resource use and what
/// Auto Performance did. Nothing is uploaded; the user decides whether to share the file.
/// </summary>
public static class DiagnosticsExport
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VIZEN Diagnostics");

    public static string Write(CaptureService capture, PerformanceMonitor monitor, AutoPerformanceService auto, AppSettings settings)
    {
        var system = SystemCompatibility.Detect(capture.IsSupported);
        var report = new
        {
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            app = new { name = "VIZEN", version = SettingsViewModel.AppVersion },
            system = new
            {
                cpu = system.CpuName,
                logicalProcessors = system.LogicalProcessors,
                memoryGb = Math.Round(system.MemoryGb, 1),
                gpus = system.Gpus,
                displays = system.Displays,
                windows = system.WindowsVersion,
                tierEstimate = SystemCompatibility.EstimateTier(system).ToString(),
                onBattery = system.OnBattery,
                mainDisplayHz = MainDisplay.DisplayHz,
            },
            settings = new { settings.AutoPerformance, settings.AllowFpsReduction },
            captures = capture.ActiveCaptures.Select(a => new
            {
                source = a.Source.Title,
                app = a.Source.ProcessName,
                kind = a.Source.IsSynthetic ? "synthetic" : a.Source.IsCamera ? "camera" : "window",
                consumers = a.Consumers,
                pipeline = a.Capture.PipelineDescription,
                fpsLimit = a.Capture.MaxFps,
                autoFpsCap = a.Capture.AutoFpsCap,
                frameMarker = a.Capture.HasFrameMarker,
                seconds = a.Capture.History.Select(h => new { t = h.At.ToString("HH:mm:ss"), h.Stats }),
            }),
            resources = monitor.History.Select(r => new { t = r.At.ToString("HH:mm:ss"), r.Snapshot }),
            autoPerformanceLog = auto.Log.Select(e => $"{e.TimeText} {e.Detail}"),
        };

        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"vizen-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        File.WriteAllText(path, JsonSerializer.Serialize(report, options));
        return path;
    }
}
