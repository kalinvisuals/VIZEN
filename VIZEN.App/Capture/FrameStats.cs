using System.Diagnostics;

namespace VIZEN.App.Capture;

/// <summary>How a capture is doing right now, and — when frames are lost — where in the pipeline.</summary>
public enum CaptureHealth
{
    Starting,

    /// <summary>Frames flow and VIZEN keeps up with the source.</summary>
    Live,

    /// <summary>No new frames for a while: the source is static or frozen (Windows only sends changes).</summary>
    NoNewFrames,

    /// <summary>The source window is minimized; Windows does not render it.</summary>
    Paused,

    /// <summary>
    /// The source itself delivers few frames (a 24/25/30 fps video, a slow camera, an app that renders slowly).
    /// Only reported when VIZEN's capture thread is mostly idle, so Windows is not being held back by VIZEN.
    /// </summary>
    SourceLimited,

    /// <summary>A frame-rate limit is active on purpose (the 24 fps preview, or Auto Performance).</summary>
    Capped,

    /// <summary>The capture thread is busy most of the time waiting for the GPU copy: Windows has to hold frames back.</summary>
    CaptureLimited,

    /// <summary>The capture thread is busy most of the time copying frame data (very large source).</summary>
    ProcessingLimited,

    /// <summary>Frames are lost because VIZEN's UI thread is too busy to display them in time.</summary>
    RenderLimited,

    /// <summary>Frames are lost because the display loop (WPF composition) runs slower than the screen.</summary>
    PresentationLimited,

    /// <summary>Frames are lost but the measurements do not point to one cause.</summary>
    Unknown,

    Ended,
}

/// <summary>
/// One measurement window (about a second) of a capture. Every value is measured, nothing is estimated:
/// <see cref="LatencyMs"/> is from the moment Windows hands a frame to VIZEN until VIZEN puts it into the bitmap it
/// displays (the time Windows needs to compose the source before, and the screen after, cannot be timed by an app).
/// </summary>
public sealed record CaptureStats(
    double SourceFps,
    double CapturedFps,
    double PresentedFps,
    double CappedFps,
    double DroppedPercent,
    double? LatencyMs,
    double? LatencyMaxMs,
    double JitterMs,
    double CopyMs,
    double PresentMs,
    double FrameAgeMs,
    CaptureHealth Health,
    int Width,
    int Height,
    double CaptureBusyPercent = 0,
    double GpuWaitMs = 0,
    double UiDelayMs = 0,
    double RenderLoopFps = 0,
    double DisplayHz = 0,
    int? SkippedFrames = null,
    int? RepeatedFrames = null,
    double FrameTimeP50Ms = 0,
    double FrameTimeP95Ms = 0,
    double FrameTimeP99Ms = 0)
{
    public static CaptureStats Empty { get; } = new(0, 0, 0, 0, 0, null, null, 0, 0, 0, 0, CaptureHealth.Starting, 0, 0);

    /// <summary>True when frames are lost because of VIZEN (a known stage), not the source.</summary>
    public static bool IsVizenLimited(CaptureHealth health) => health is CaptureHealth.CaptureLimited
        or CaptureHealth.ProcessingLimited or CaptureHealth.RenderLimited or CaptureHealth.PresentationLimited;
}

/// <summary>
/// Lock-light frame counters for one capture. Capture-thread methods use Interlocked; presentation and rolling
/// happen on the UI thread. Timestamps are Stopwatch (QPC) ticks, the same clock Windows uses for frame times.
/// </summary>
public sealed class FrameStats
{
    /// <summary>Below this the source itself may be the limit (cinema 24, PAL 25 and 30 fps sources included).</summary>
    public const double SourceLimitedBelowFps = 31;

    /// <summary>More than this share of delivered frames not shown = frames are being lost.</summary>
    public const double LostFramesPercent = 5;

    /// <summary>Capture thread busy at least this share of the time = Windows is being held back by VIZEN.</summary>
    public const double CaptureSaturated = 0.8;

    /// <summary>Capture thread busy below this share = Windows can always hand over frames, so a low rate is the source's.</summary>
    public const double CaptureIdle = 0.5;

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    // Capture thread (Interlocked)
    private int _arrived;
    private int _capped;
    private int _copied;
    private long _copyTicks;
    private long _busyTicks;
    private long _gpuWaitTicks;
    private int _workFrames;
    private long _lastSourceFrameAt;

    // Presentation side: the UI thread (WPF images) or a capture thread (native presenters); guarded by _presentGate.
    private readonly object _presentGate = new();
    private int _presented;
    private long _presentTicks;
    private double _latencySumMs;
    private double _latencyMaxMs;
    private int _latencyCount;
    private double _uiDelaySumMs;
    private int _uiDelayCount;
    private long _lastPresentAt;
    private double _intervalSum;
    private double _intervalSumSq;
    private int _intervalCount;
    private readonly List<double> _intervals = new(256);
    private long _lastMarker = -1;
    private int _skipped;
    private int _repeated;
    private int _markerFrames;
    private long _windowStart = Stopwatch.GetTimestamp();

    public void OnArrived(long now)
    {
        Interlocked.Increment(ref _arrived);
        Interlocked.Exchange(ref _lastSourceFrameAt, now);
    }

    public void OnCapped() => Interlocked.Increment(ref _capped);

    public void OnCopied(long copyTicks)
    {
        Interlocked.Increment(ref _copied);
        Interlocked.Add(ref _copyTicks, copyTicks);
    }

    /// <summary>Capture thread: total time spent on one frame (from Windows' callback to releasing it) and the GPU-wait part.</summary>
    public void OnCaptureWork(long busyTicks, long gpuWaitTicks)
    {
        Interlocked.Increment(ref _workFrames);
        Interlocked.Add(ref _busyTicks, busyTicks);
        Interlocked.Add(ref _gpuWaitTicks, gpuWaitTicks);
    }

    /// <param name="sourceTimestamp">QPC time VIZEN received the frame from Windows, or 0 when unknown.</param>
    /// <param name="uiDelayTicks">Time the frame waited for the UI thread after it was ready.</param>
    public void OnPresented(long now, long sourceTimestamp, long presentTicks, long uiDelayTicks = 0)
    {
        lock (_presentGate)
        {
            OnPresentedLocked(now, sourceTimestamp, presentTicks, uiDelayTicks);
        }
    }

    private void OnPresentedLocked(long now, long sourceTimestamp, long presentTicks, long uiDelayTicks)
    {
        _presented++;
        _presentTicks += presentTicks;
        if (uiDelayTicks > 0)
        {
            _uiDelaySumMs += uiDelayTicks * TicksToMs;
            _uiDelayCount++;
        }

        if (sourceTimestamp > 0 && now >= sourceTimestamp)
        {
            var latency = (now - sourceTimestamp) * TicksToMs;
            if (latency < 10_000)
            {
                _latencySumMs += latency;
                _latencyMaxMs = Math.Max(_latencyMaxMs, latency);
                _latencyCount++;
            }
        }

        if (_lastPresentAt > 0)
        {
            var interval = (now - _lastPresentAt) * TicksToMs;
            if (interval < 1000)
            {
                _intervalSum += interval;
                _intervalSumSq += interval * interval;
                _intervalCount++;
                _intervals.Add(interval);
            }
        }

        _lastPresentAt = now;
    }

    /// <summary>UI thread: frame number read from a test source's marker on the displayed frame.</summary>
    public void OnMarker(uint index)
    {
        lock (_presentGate)
        {
            OnMarkerLocked(index);
        }
    }

    private void OnMarkerLocked(uint index)
    {
        if (_lastMarker >= 0)
        {
            var gap = (long)index - _lastMarker;
            if (gap == 0)
            {
                _repeated++;
            }
            else if (gap > 1 && gap < 100_000)
            {
                _skipped += (int)(gap - 1);
            }
        }

        _lastMarker = index;
        _markerFrames++;
    }

    /// <summary>Closes the current window and returns its numbers (UI thread).</summary>
    /// <param name="uiPresentFraction">
    /// Share of the time the (single) UI thread spent presenting frames of all captures; negative = use this capture's own.
    /// </param>
    public CaptureStats Roll(long now, bool isLive, bool isMinimized, bool hasEnded, double maxFps, int width, int height,
        double renderLoopFps = 0, double displayHz = 0, double uiPresentFraction = -1)
    {
        lock (_presentGate)
        {
            return RollLocked(now, isLive, isMinimized, hasEnded, maxFps, width, height, renderLoopFps, displayHz, uiPresentFraction);
        }
    }

    private CaptureStats RollLocked(long now, bool isLive, bool isMinimized, bool hasEnded, double maxFps, int width, int height,
        double renderLoopFps, double displayHz, double uiPresentFraction)
    {
        var elapsedTicks = Math.Max(1, now - _windowStart);
        var seconds = elapsedTicks * TicksToMs / 1000;
        _windowStart = now;
        var arrived = Interlocked.Exchange(ref _arrived, 0);
        var capped = Interlocked.Exchange(ref _capped, 0);
        var copied = Interlocked.Exchange(ref _copied, 0);
        var copyTicks = Interlocked.Exchange(ref _copyTicks, 0);
        var busyTicks = Interlocked.Exchange(ref _busyTicks, 0);
        var gpuWaitTicks = Interlocked.Exchange(ref _gpuWaitTicks, 0);
        var workFrames = Interlocked.Exchange(ref _workFrames, 0);
        var presented = _presented;
        var lastSource = Interlocked.Read(ref _lastSourceFrameAt);

        var dropped = Math.Max(0, copied - presented);
        var droppedPercent = copied > 0 ? 100.0 * dropped / copied : 0;
        double? latency = _latencyCount > 0 ? _latencySumMs / _latencyCount : null;
        double? latencyMax = _latencyCount > 0 ? _latencyMaxMs : null;
        var jitter = 0.0;
        if (_intervalCount > 1)
        {
            var mean = _intervalSum / _intervalCount;
            jitter = Math.Sqrt(Math.Max(0, _intervalSumSq / _intervalCount - mean * mean));
        }

        var busyFraction = Math.Min(1, busyTicks / (double)elapsedTicks);
        var gpuWaitFraction = Math.Min(1, gpuWaitTicks / (double)elapsedTicks);
        var presentFraction = uiPresentFraction >= 0 ? Math.Min(1, uiPresentFraction) : Math.Min(1, _presentTicks / (double)elapsedTicks);
        var uiDelay = _uiDelayCount > 0 ? _uiDelaySumMs / _uiDelayCount : 0;
        var frameAge = lastSource > 0 ? (now - lastSource) * TicksToMs : 0;
        var stats = new CaptureStats(
            SourceFps: arrived / seconds,
            CapturedFps: copied / seconds,
            PresentedFps: presented / seconds,
            CappedFps: capped / seconds,
            DroppedPercent: droppedPercent,
            LatencyMs: latency,
            LatencyMaxMs: latencyMax,
            JitterMs: jitter,
            CopyMs: copied > 0 ? copyTicks * TicksToMs / copied : 0,
            PresentMs: presented > 0 ? _presentTicks * TicksToMs / presented : 0,
            FrameAgeMs: frameAge,
            Health: Classify(arrived / seconds, capped, droppedPercent, presented / seconds, frameAge, isLive, isMinimized,
                hasEnded, maxFps, busyFraction, gpuWaitFraction, uiDelay, presentFraction, renderLoopFps, displayHz),
            Width: width,
            Height: height,
            CaptureBusyPercent: busyFraction * 100,
            GpuWaitMs: workFrames > 0 ? gpuWaitTicks * TicksToMs / workFrames : 0,
            UiDelayMs: uiDelay,
            RenderLoopFps: renderLoopFps,
            DisplayHz: displayHz,
            SkippedFrames: _markerFrames > 0 ? _skipped : null,
            RepeatedFrames: _markerFrames > 0 ? _repeated : null,
            FrameTimeP50Ms: Percentile(_intervals, 0.50),
            FrameTimeP95Ms: Percentile(_intervals, 0.95),
            FrameTimeP99Ms: Percentile(_intervals, 0.99));

        _presented = 0;
        _presentTicks = 0;
        _latencySumMs = 0;
        _latencyMaxMs = 0;
        _latencyCount = 0;
        _uiDelaySumMs = 0;
        _uiDelayCount = 0;
        _intervalSum = 0;
        _intervalSumSq = 0;
        _intervalCount = 0;
        _intervals.Clear();
        _skipped = 0;
        _repeated = 0;
        _markerFrames = 0;
        return stats;
    }

    /// <summary>Nearest-rank percentile of the time between displayed frames (0 when there are none). Sorts in place.</summary>
    internal static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        values.Sort();
        var rank = (int)Math.Ceiling(p * values.Count) - 1;
        return values[Math.Clamp(rank, 0, values.Count - 1)];
    }

    /// <summary>
    /// Decides where frames are lost, from measured values only. "Source limited" needs evidence: a low arrival
    /// rate while VIZEN's capture thread is mostly idle. When the evidence is mixed the answer is Unknown.
    /// </summary>
    internal static CaptureHealth Classify(double sourceFps, int capped, double droppedPercent, double presentedFps,
        double frameAgeMs, bool isLive, bool isMinimized, bool hasEnded, double maxFps,
        double captureBusyFraction = 0, double gpuWaitFraction = 0, double uiDelayMs = 0, double presentBusyFraction = 0,
        double renderLoopFps = 0, double displayHz = 0)
    {
        if (hasEnded)
        {
            return CaptureHealth.Ended;
        }

        if (isMinimized)
        {
            return CaptureHealth.Paused;
        }

        if (!isLive)
        {
            return CaptureHealth.Starting;
        }

        if (sourceFps < 0.5 && frameAgeMs > 2000)
        {
            return CaptureHealth.NoNewFrames;
        }

        // The capture thread is (almost) never free: Windows cannot hand over frames as fast as it renders them.
        if (captureBusyFraction >= CaptureSaturated)
        {
            return gpuWaitFraction >= captureBusyFraction / 2 ? CaptureHealth.CaptureLimited : CaptureHealth.ProcessingLimited;
        }

        if (droppedPercent > LostFramesPercent && sourceFps >= 5)
        {
            var interval = 1000 / Math.Max(1, sourceFps);
            if (presentBusyFraction >= 0.6 || uiDelayMs > Math.Max(8, interval * 0.5))
            {
                return CaptureHealth.RenderLimited;
            }

            if (renderLoopFps > 0 && displayHz > 0 && renderLoopFps < 0.8 * Math.Min(displayHz, sourceFps))
            {
                return CaptureHealth.PresentationLimited;
            }

            return CaptureHealth.Unknown;
        }

        if (maxFps > 0 && capped > 0)
        {
            return CaptureHealth.Capped;
        }

        if (sourceFps < SourceLimitedBelowFps && sourceFps >= 0.5)
        {
            return captureBusyFraction < CaptureIdle ? CaptureHealth.SourceLimited : CaptureHealth.Unknown;
        }

        return CaptureHealth.Live;
    }
}
