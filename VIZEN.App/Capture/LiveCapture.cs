using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using Windows.Graphics;

namespace VIZEN.App.Capture;

public enum CaptureStatus
{
    Starting,
    Live,
    SourceClosed,
    Failed,
    Stopped,
}

/// <summary>
/// Shared live-capture engine: frames arrive on a worker thread (from a window or a camera), are copied
/// into a CPU buffer and presented on the UI thread in a <see cref="WriteableBitmap"/>. If the UI falls
/// behind, intermediate frames are dropped rather than queued. Previews, overlays, region crops and OBS
/// outputs all render this one bitmap.
/// </summary>
public abstract class LiveCapture : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _fpsTimer;
    private long _minFrameTicks;
    private double _maxFps;
    private double _requestedFps;
    private double _autoFpsCap;
    private long _lastFrameTicks;

    // Latest frame handed from the capture thread to the UI thread (triple buffer: neither side waits for the other).
    private readonly FrameHandoff _handoff = new();
    private int _renderQueued;
    private readonly Queue<(DateTime At, CaptureStats Stats)> _history = new();
    private const int HistorySeconds = 300;

    // FPS measurement (UI thread only).
    private int _framesThisSecond;
    private long _fpsWindowStart = Stopwatch.GetTimestamp();

    private ImageSource? _frame;
    private WriteableBitmap? _cpuBitmap;
    private int _frameWidth;
    private int _frameHeight;

    // Size of the newest frame drawn natively (capture thread), and whether the UI already shows that size.
    private int _nativeWidth;
    private int _nativeHeight;
    private bool _hasNativeFrame;
    private long _cpuFramesWritten;

    // UI-thread time spent presenting frames of all captures together (one UI thread serves every overlay).
    private static long s_presentTicksAll;
    private long _presentTicksAllAtRoll;
    private CaptureStatus _status = CaptureStatus.Starting;
    private string? _errorMessage;
    private int _fps;
    private bool _isSourceMinimized;
    private volatile bool _isDisposed;
    private static readonly string? DiagnosticsFile =
        Environment.GetEnvironmentVariable("VIZEN_DIAG_FILE") is { Length: > 0 } path ? path : null;

    private readonly FrameStats _stats = new();
    private CaptureStats _lastStats = CaptureStats.Empty;

    protected LiveCapture(object key, double maxFps)
    {
        Key = key;
        Dispatcher = Dispatcher.CurrentDispatcher;
        RequestedFps = maxFps;
        _fpsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnFpsTimerTick, Dispatcher);
        RestoreSourceCommand = new RelayCommand(RestoreSource, () => SupportsWindowActions && IsSourceMinimized);
        ShowSourceCommand = new RelayCommand(ShowSource, () => SupportsWindowActions);
    }

    /// <summary>True for window sources: they can be restored/shown and kept active. Cameras cannot.</summary>
    public virtual bool SupportsWindowActions => false;

    /// <summary>
    /// Per-app opt-in: when the source window gets minimized, restore it behind the active window so
    /// capture continues. Always false for sources that do not support it.
    /// </summary>
    public virtual bool KeepSourceActive
    {
        get => false;
        set { }
    }

    /// <summary>Restores a minimized source behind the active window, without taking focus.</summary>
    public ICommand RestoreSourceCommand { get; }

    /// <summary>Brings the source window to the front (explicit user request; takes focus).</summary>
    public ICommand ShowSourceCommand { get; }

    protected virtual void RestoreSource()
    {
    }

    protected virtual void ShowSource()
    {
    }

    /// <summary>Identity of the captured source (window handle or camera id); one capture is shared per key.</summary>
    public object Key { get; }

    protected Dispatcher Dispatcher { get; }

    protected bool IsDisposed => _isDisposed;

    /// <summary>
    /// Latest captured frame as a WriteableBitmap, for in-app previews (overlays draw natively). Raised again when the
    /// frame size changes. Use <see cref="FrameWidth"/>/<see cref="FrameHeight"/> for its pixel size.
    /// </summary>
    public ImageSource? Frame
    {
        get => _frame;
        private set
        {
            if (SetProperty(ref _frame, value))
            {
                OnPropertyChanged(nameof(HasFrame));
            }
        }
    }

    /// <summary>Pixel width of <see cref="Frame"/> (0 before the first frame).</summary>
    public int FrameWidth => _frameWidth;

    /// <summary>Pixel height of <see cref="Frame"/> (0 before the first frame).</summary>
    public int FrameHeight => _frameHeight;

    private void SetFrame(ImageSource image, int width, int height)
    {
        var sizeChanged = width != _frameWidth || height != _frameHeight;
        _frameWidth = width;
        _frameHeight = height;
        if (!ReferenceEquals(_frame, image))
        {
            Frame = image;
        }
        else if (sizeChanged)
        {
            OnPropertyChanged(nameof(Frame));
        }

        if (sizeChanged)
        {
            OnPropertyChanged(nameof(FrameWidth));
            OnPropertyChanged(nameof(FrameHeight));
        }
    }

    /// <summary>
    /// True while native presenters (overlay windows with their own swap chain) show this capture. Their frames are
    /// the ones counted as shown; the CPU copy for in-app previews then only refreshes the WPF image.
    /// </summary>
    protected volatile bool NativePresenting;

    private double _cpuFrameMaxFps;

    /// <summary>
    /// Rate of the CPU copy for WPF consumers while native presenters show the capture (0 = every frame, negative = none).
    /// Set by <see cref="CaptureService"/> from the consumers' needs. Thread-safe.
    /// </summary>
    public double CpuFrameMaxFps
    {
        get => Volatile.Read(ref _cpuFrameMaxFps);
        internal set => Volatile.Write(ref _cpuFrameMaxFps, value);
    }

    /// <summary>Capture thread: releases the CPU frame buffers (no CPU consumer for a while).</summary>
    protected void TrimCpuBuffers()
    {
        _handoff.Trim();
        var cpuFramesAtTrim = Interlocked.Read(ref _cpuFramesWritten);
        Dispatcher.BeginInvoke(() =>
        {
            // Still no CPU frame since the trim: nobody shows the WPF image, drop it (and its GPU copy inside WPF).
            if (!_isDisposed && Interlocked.Read(ref _cpuFramesWritten) == cpuFramesAtTrim && _frame is not null)
            {
                _cpuBitmap = null;
                Frame = null;
            }

            ReturnFreedMemory();
        }, DispatcherPriority.Background);
    }

    private static long s_lastCompaction;

    /// <summary>
    /// The frame buffers are large objects, which .NET only frees in a full collection — and with steady GPU-only
    /// capture that may never come, so the freed buffers would keep their RAM. Collect once (at most every 2 s).
    /// </summary>
    private static void ReturnFreedMemory()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - s_lastCompaction < Stopwatch.Frequency * 2)
        {
            return;
        }

        s_lastCompaction = now;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>
    /// Capture thread: one frame went through the GPU path (copied into the ring and, when <paramref name="presented"/>,
    /// drawn and presented by the native presenters).
    /// </summary>
    /// <summary>
    /// Capture thread: size of the newest frame on the GPU path. Native presenters can show frames without any WPF image,
    /// so <see cref="FrameWidth"/>, <see cref="FrameHeight"/> and <see cref="HasFrame"/> follow the GPU frames too.
    /// </summary>
    protected void ReportNativeSize(int width, int height)
    {
        if (width == _nativeWidth && height == _nativeHeight)
        {
            return;
        }

        _nativeWidth = width;
        _nativeHeight = height;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            var hadFrame = HasFrame;
            _hasNativeFrame = true;
            if (width != _frameWidth || height != _frameHeight)
            {
                _frameWidth = width;
                _frameHeight = height;
                OnPropertyChanged(nameof(FrameWidth));
                OnPropertyChanged(nameof(FrameHeight));
                OnPropertyChanged(nameof(Frame)); // listeners use it as "the frame size changed"
            }

            if (!hadFrame)
            {
                OnPropertyChanged(nameof(HasFrame));
            }
        });
    }

    protected void ReportNativeFrame(long copyTicks, long sourceTime, long presentTicks, bool presented, uint? marker)
    {
        _stats.OnCopied(copyTicks);
        if (!presented)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _stats.OnPresented(now, sourceTime, presentTicks);
        if (marker is { } index)
        {
            _stats.OnMarker(index);
        }

        Interlocked.Increment(ref _framesThisSecond);
        if (Status == CaptureStatus.Starting)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (Status == CaptureStatus.Starting)
                {
                    Status = CaptureStatus.Live;
                }
            });
        }
    }

    /// <summary>True once there is a frame: a WPF image in <see cref="Frame"/>, or a frame drawn by native presenters.</summary>
    public bool HasFrame => Frame is not null || _hasNativeFrame;

    /// <summary>Measurements of the last second (updated once a second on the UI thread).</summary>
    public CaptureStats Stats
    {
        get => _lastStats;
        private set
        {
            _lastStats = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PerformanceText));
            OnPropertyChanged(nameof(HealthText));
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(Health));
        }
    }

    public CaptureHealth Health => _lastStats.Health;

    /// <summary>"Source 60 fps · Shown 59.8 fps · Dropped 0.3% · Latency 18 ms (measured)".</summary>
    public string PerformanceText => PerformanceFormatter.Format(_lastStats);

    public string HealthText => PerformanceFormatter.Health(_lastStats);

    /// <summary>Compact text for the optional on-overlay indicator.</summary>
    public string BadgeText => PerformanceFormatter.Badge(_lastStats);

    public CaptureStatus Status
    {
        get => _status;
        protected set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsLive));
            }
        }
    }

    public bool IsLive => Status == CaptureStatus.Live;

    /// <summary>False once the capture has ended (source closed, failed or stopped) and must be replaced.</summary>
    public bool IsUsable => Status is CaptureStatus.Starting or CaptureStatus.Live;

    /// <summary>Effective upper bound on frames copied per second; 0 = as fast as the source delivers.</summary>
    public double MaxFps => _maxFps;

    /// <summary>Cap the consumers need (the most demanding one wins); 0 = uncapped. UI thread.</summary>
    internal double RequestedFps
    {
        get => _requestedFps;
        set
        {
            _requestedFps = value;
            UpdateFrameCap();
        }
    }

    /// <summary>Temporary cap set by Auto Performance (only when the user allowed it); 0 = none. UI thread.</summary>
    public double AutoFpsCap
    {
        get => _autoFpsCap;
        internal set
        {
            if (_autoFpsCap.Equals(value))
            {
                return;
            }

            _autoFpsCap = value;
            UpdateFrameCap();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAutoCapped));
        }
    }

    /// <summary>True while Auto Performance actually lowers this capture's frame rate.</summary>
    public bool IsAutoCapped => _autoFpsCap > 0 && (_requestedFps <= 0 || _autoFpsCap < _requestedFps);

    private void UpdateFrameCap()
    {
        var cap = IsAutoCapped ? _autoFpsCap : _requestedFps;
        _maxFps = cap;
        Interlocked.Exchange(ref _minFrameTicks, cap > 0 ? (long)(Stopwatch.Frequency / cap) : 0);
        OnPropertyChanged(nameof(MaxFps));
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public int Fps
    {
        get => _fps;
        private set
        {
            if (SetProperty(ref _fps, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>True when the source cannot produce frames right now because it is minimized (windows only).</summary>
    public bool IsSourceMinimized
    {
        get => _isSourceMinimized;
        protected set
        {
            if (SetProperty(ref _isSourceMinimized, value))
            {
                OnPropertyChanged(nameof(StatusText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string StatusText => Status switch
    {
        CaptureStatus.Starting or CaptureStatus.Live when IsSourceMinimized => Loc.T("Capture.Paused"),
        CaptureStatus.Starting => Loc.T("Capture.Waiting"),
        // Windows only delivers window frames when the source repaints, so a static window reads as idle.
        CaptureStatus.Live => Fps > 0 ? Loc.F("Capture.LiveFps", Fps) : Loc.T("Capture.LiveIdle"),
        CaptureStatus.SourceClosed => Loc.T("Capture.SourceClosed"),
        CaptureStatus.Failed => ErrorMessage ?? Loc.T("Capture.Failed"),
        _ => Loc.T("Capture.Stopped"),
    };

    /// <summary>Size of the source in physical pixels, or default when unknown.</summary>
    public virtual SizeInt32 SourceSize => _frameWidth > 0 ? new SizeInt32 { Width = _frameWidth, Height = _frameHeight } : default;

    /// <summary>Raised on the UI thread when the source goes away (window closed, camera unplugged).</summary>
    public event EventHandler? SourceClosed;

    /// <summary>Capture thread: false when this frame should be skipped to respect <see cref="MaxFps"/>.</summary>
    protected bool TryBeginFrame()
    {
        var now = Stopwatch.GetTimestamp();
        _stats.OnArrived(now);
        var minFrameTicks = Interlocked.Read(ref _minFrameTicks);
        if (minFrameTicks > 0 && now - _lastFrameTicks < minFrameTicks)
        {
            _stats.OnCapped();
            return false;
        }

        _lastFrameTicks = now;
        return true;
    }

    /// <summary>
    /// Capture thread: hands over one BGRA frame. <paramref name="fill"/> copies stride × height bytes into the
    /// provided buffer. Presentation on the UI thread is queued automatically.
    /// </summary>
    /// <param name="sourceTimestamp">QPC ticks when Windows produced the frame (for measured latency), or 0.</param>
    protected void WriteFrame(int width, int height, int stride, Action<byte[], int> fill, long sourceTimestamp = 0)
    {
        if (width <= 0 || height <= 0 || _isDisposed)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var length = stride * height;
        Interlocked.Increment(ref _cpuFramesWritten);
        var slot = _handoff.WriteSlot;
        slot.Ensure(length);
        fill(slot.Data, length);
        Publish(slot, width, height, stride, sourceTimestamp, start);
    }

    /// <summary>Capture thread: copies one BGRA frame from native memory (no per-frame allocation).</summary>
    protected void WriteFrame(int width, int height, int stride, IntPtr source, long sourceTimestamp)
    {
        if (width <= 0 || height <= 0 || _isDisposed || source == IntPtr.Zero)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var length = stride * height;
        Interlocked.Increment(ref _cpuFramesWritten);
        var slot = _handoff.WriteSlot;
        slot.Ensure(length);
        System.Runtime.InteropServices.Marshal.Copy(source, slot.Data, 0, length);
        Publish(slot, width, height, stride, sourceTimestamp, start);
    }

    /// <summary>
    /// Capture thread: time spent on one frame from Windows' callback until the frame was given back, and how much
    /// of it was waiting for the GPU. Lets the diagnostics tell "VIZEN holds Windows back" from "the source is slow".
    /// </summary>
    protected void ReportCaptureWork(long busyTicks, long gpuWaitTicks) => _stats.OnCaptureWork(busyTicks, gpuWaitTicks);

    /// <summary>True for VIZEN's own test sources, whose frames carry a frame-number marker (see <see cref="FrameMarker"/>).</summary>
    public bool HasFrameMarker { get; protected set; }

    /// <summary>The last <see cref="HistorySeconds"/> seconds of measurements (for the diagnostics export). UI thread.</summary>
    public IReadOnlyList<(DateTime At, CaptureStats Stats)> History => [.. _history];

    /// <summary>How frames reach the screen (shown in diagnostics).</summary>
    public virtual string PipelineDescription =>
        "GPU→CPU copy, triple buffer, WPF WriteableBitmap (latest frame only)";

    /// <summary>Converts a Windows frame time (100 ns units of the QPC clock) to Stopwatch ticks.</summary>
    protected static long ToStopwatchTicks(TimeSpan systemRelativeTime) =>
        (long)(systemRelativeTime.Ticks * (Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond));

    private void Publish(FrameSlot slot, int width, int height, int stride, long sourceTimestamp, long copyStart)
    {
        slot.Width = width;
        slot.Height = height;
        slot.Stride = stride;
        slot.SourceTime = sourceTimestamp;
        var now = Stopwatch.GetTimestamp();
        if (!NativePresenting)
        {
            _stats.OnCopied(now - copyStart);
        }

        _handoff.Publish(now);
        QueuePresent();
    }

    private void QueuePresent()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, PresentFrame);
        }
    }

    /// <summary>UI thread: marks the capture as failed with a user-facing message.</summary>
    protected void Fail(string message)
    {
        ErrorMessage = message;
        Status = CaptureStatus.Failed;
        StopFpsTimer();
    }

    /// <summary>UI thread: the source disappeared.</summary>
    protected void MarkSourceClosed()
    {
        Status = CaptureStatus.SourceClosed;
        StopFpsTimer();
        SourceClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called once per second on the UI thread; return true if the source is minimized.</summary>
    protected virtual bool CheckSourceMinimized() => false;

    /// <summary>Releases the source-specific capture objects. May be called more than once.</summary>
    protected abstract void ReleaseResources();

    private void StopFpsTimer()
    {
        _fpsTimer.Stop();
        Fps = 0;
    }

    /// <summary>UI thread: copies the latest buffered frame into the bitmap.</summary>
    private void PresentFrame()
    {
        Interlocked.Exchange(ref _renderQueued, 0);
        if (_isDisposed)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var slot = _handoff.TakeLatest();
        if (slot is null)
        {
            return;
        }

        // The capture thread keeps writing into its own slot meanwhile: no lock is held during the upload.
        if (_cpuBitmap is null || _cpuBitmap.PixelWidth != slot.Width || _cpuBitmap.PixelHeight != slot.Height)
        {
            // Bgr32 ignores alpha: some windows report undefined alpha in their client area.
            _cpuBitmap = new WriteableBitmap(slot.Width, slot.Height, 96, 96, PixelFormats.Bgr32, null);
        }

        _cpuBitmap.WritePixels(new Int32Rect(0, 0, slot.Width, slot.Height), slot.Data, slot.Stride, 0);
        SetFrame(_cpuBitmap, slot.Width, slot.Height);
        var end = Stopwatch.GetTimestamp();
        s_presentTicksAll += end - start;
        if (NativePresenting)
        {
            // Native presenters count the shown frames; this copy only refreshes in-app previews.
            if (Status == CaptureStatus.Starting)
            {
                Status = CaptureStatus.Live;
            }

            return;
        }

        if (HasFrameMarker && FrameMarker.Decode(slot.Data, slot.Width, slot.Height, slot.Stride) is { } index)
        {
            _stats.OnMarker(index);
        }

        _stats.OnPresented(end, slot.SourceTime, end - start, start - slot.QueuedAt);
        MarkPresented();
    }

    private void MarkPresented()
    {

        if (Status == CaptureStatus.Starting)
        {
            Status = CaptureStatus.Live;
        }

        Interlocked.Increment(ref _framesThisSecond);
    }

    private void OnFpsTimerTick(object? sender, EventArgs e)
    {
        IsSourceMinimized = CheckSourceMinimized();
        var rollNow = Stopwatch.GetTimestamp();
        var uiPresentFraction = (s_presentTicksAll - _presentTicksAllAtRoll) / (double)Math.Max(1, rollNow - _fpsWindowStart);
        _presentTicksAllAtRoll = s_presentTicksAll;
        Stats = _stats.Roll(rollNow, IsLive, IsSourceMinimized, !IsUsable, MaxFps,
            _frameWidth, _frameHeight, displayHz: MainDisplay.DisplayHz, uiPresentFraction: uiPresentFraction);
        _history.Enqueue((DateTime.Now, Stats));
        while (_history.Count > HistorySeconds)
        {
            _history.Dequeue();
        }

        // Opt-in diagnostics: set VIZEN_DIAG_FILE to a file path to log every capture's measurements once a second.
        if (DiagnosticsFile is { } diagFile)
        {
            var s = Stats;
            System.IO.File.AppendAllText(diagFile, $"{DateTime.Now:HH:mm:ss} {Key} maxFps={MaxFps} source={s.SourceFps:0.0} captured={s.CapturedFps:0.0} presented={s.PresentedFps:0.0} dropped={s.DroppedPercent:0.0}% latency={s.LatencyMs:0.0}ms max={s.LatencyMaxMs:0.0} jitter={s.JitterMs:0.0}ms copy={s.CopyMs:0.00}ms present={s.PresentMs:0.00}ms busy={s.CaptureBusyPercent:0}% gpuwait={s.GpuWaitMs:0.00}ms uidelay={s.UiDelayMs:0.0}ms heap={GC.GetTotalMemory(false) / 1048576.0:0.0}MB gccommit={GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0:0.0}MB p50={s.FrameTimeP50Ms:0.0} p95={s.FrameTimeP95Ms:0.0} p99={s.FrameTimeP99Ms:0.0} skipped={s.SkippedFrames} repeated={s.RepeatedFrames} {s.Width}x{s.Height} {s.Health}{Environment.NewLine}");
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = now - _fpsWindowStart;
        var frames = Interlocked.Exchange(ref _framesThisSecond, 0);
        Fps = elapsed > 0 ? (int)Math.Round(frames * (double)Stopwatch.Frequency / elapsed) : 0;
        _fpsWindowStart = now;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        ReleaseResources();
        StopFpsTimer();

        // Drop the frame buffers now: a closed capture can stay referenced (bindings, lists) until the next GC,
        // and its 1080p bitmap and CPU slots would keep RAM and GPU memory with it.
        _handoff.Trim();
        _cpuBitmap = null;
        Frame = null;
        if (Status is CaptureStatus.Starting or CaptureStatus.Live)
        {
            Status = CaptureStatus.Stopped;
        }

        GC.SuppressFinalize(this);
    }
}
