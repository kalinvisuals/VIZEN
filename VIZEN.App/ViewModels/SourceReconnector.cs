using VIZEN.App.Capture;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>
/// Long sessions: when a captured window closes and a matching one comes back (a restarted call, a reopened
/// chat), overlays, OBS outputs and composition layers continue on the new window by themselves. Runs after
/// every source scan. Only a window of the same app with the same or a similar title is accepted.
/// </summary>
public sealed class SourceReconnector
{
    private readonly WindowSourcesViewModel _sources;
    private readonly OverlaysViewModel _overlays;
    private readonly OutputsViewModel _outputs;
    private readonly CompositionsViewModel _compositions;
    private readonly CaptureService _capture;
    private readonly ActivityLog _activity;

    public SourceReconnector(WindowSourcesViewModel sources, OverlaysViewModel overlays, OutputsViewModel outputs,
        CompositionsViewModel compositions, CaptureService capture, ActivityLog activity)
    {
        _sources = sources;
        _overlays = overlays;
        _outputs = outputs;
        _compositions = compositions;
        _capture = capture;
        _activity = activity;
        sources.Refreshed += (_, _) => Reconnect();
    }

    /// <summary>Reconnects everything whose source is gone; returns how many consumers were reconnected.</summary>
    public int Reconnect()
    {
        var windows = _sources.Sources.ToList();
        var count = 0;

        foreach (var overlay in _overlays.Overlays.Where(o => o.Capture.Status == CaptureStatus.SourceClosed).ToList())
        {
            if (TryAcquire(overlay.Source, windows) is var (window, lease))
            {
                overlay.Reconnect(window, lease);
                Report(overlay.Title);
                count++;
            }
        }

        foreach (var output in _outputs.Outputs.Where(o => o.Capture.Status == CaptureStatus.SourceClosed).ToList())
        {
            if (TryAcquire(output.Source, windows) is var (window, lease))
            {
                output.Reconnect(window, lease);
                Report(output.WindowTitle);
                count++;
            }
        }

        foreach (var name in _compositions.ReconnectMissing(windows))
        {
            Report(name);
            count++;
        }

        return count;
    }

    private (WindowSource Window, CaptureLease Lease)? TryAcquire(WindowSource closed, IReadOnlyList<WindowSource> windows)
    {
        if (FindReplacement(closed, windows) is not { } window || !_capture.CanCapture(window))
        {
            return null;
        }

        try
        {
            var lease = _capture.Acquire(window);
            if (lease.Capture.Status == CaptureStatus.Failed)
            {
                lease.Dispose();
                return null;
            }

            return (window, lease);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>A different window of the same app whose title is the same or similar (never just "same app").</summary>
    internal static WindowSource? FindReplacement(WindowSource closed, IReadOnlyList<WindowSource> windows)
    {
        if (closed.IsCamera)
        {
            return windows.FirstOrDefault(w => w.CameraDeviceId == closed.CameraDeviceId);
        }

        var candidates = windows.Where(w => w.Handle != closed.Handle && !w.IsCamera).ToList();
        var layout = new OverlayLayout
        {
            ProcessName = closed.ProcessName,
            ExecutablePath = closed.ExecutablePath,
            WindowTitle = closed.Title,
            ClassName = closed.ClassName,
        };
        var match = ProfileMatcher.Match([layout], candidates).Single();
        return match.Quality is MatchQuality.Exact or MatchQuality.Similar ? match.Window : null;
    }

    private void Report(string name) =>
        _activity.Add("", "Activity.SourceReconnected", "Activity.SourceReconnectedDetail", name);
}
