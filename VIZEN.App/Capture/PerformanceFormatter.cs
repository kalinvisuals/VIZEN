using System.Globalization;
using VIZEN.App.Localization;

namespace VIZEN.App.Capture;

/// <summary>Turns <see cref="CaptureStats"/> into the short, honest texts shown on cards and overlays.</summary>
public static class PerformanceFormatter
{
    public static string Health(CaptureStats s) => Loc.T("Perf.Health." + s.Health);

    public static string Format(CaptureStats s)
    {
        if (s.Health is CaptureHealth.Starting or CaptureHealth.Paused or CaptureHealth.Ended)
        {
            return Health(s);
        }

        var culture = CultureInfo.CurrentCulture;
        var latency = s.LatencyMs is { } ms
            ? Loc.F("Perf.LatencyMeasured", ms.ToString("0", culture))
            : Loc.T("Perf.LatencyUnknown");
        return Loc.F("Perf.Line", Health(s), s.SourceFps.ToString("0", culture), s.PresentedFps.ToString("0.#", culture),
            s.DroppedPercent.ToString("0.#", culture), latency);
    }

    /// <summary>Compact on-overlay badge: "LIVE · 60/60 fps · 0% · 4 ms".</summary>
    public static string Badge(CaptureStats s)
    {
        var state = Loc.T("Perf.Short." + s.Health);
        if (s.Health is CaptureHealth.Starting or CaptureHealth.Paused or CaptureHealth.Ended or CaptureHealth.NoNewFrames)
        {
            return state;
        }

        var culture = CultureInfo.CurrentCulture;
        var latency = s.LatencyMs is { } ms ? ms.ToString("0", culture) + " ms" : "—";
        return Loc.F("Perf.Badge", state, s.SourceFps.ToString("0", culture), s.PresentedFps.ToString("0", culture),
            s.DroppedPercent.ToString("0.#", culture), latency);
    }
}
