using System.Windows;
using System.Runtime.InteropServices;
using System.Text;
using VIZEN.App.Interop;
using VIZEN.App.Localization;
using VIZEN.App.Services;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace VIZEN.App.Capture;

/// <summary>
/// Live capture of a single window via Windows.Graphics.Capture. Frames are copied GPU → CPU through a
/// staging texture and handed to <see cref="LiveCapture"/> for presentation. Each capture owns its Direct3D
/// device, so captures of different windows never wait for each other.
/// </summary>
public sealed class WindowCapture : LiveCapture
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    /// <summary>
    /// Frames Windows may have ready at once. Two, as OBS uses: VIZEN keeps only the newest frame for its presenters, so
    /// Windows always has the other buffer to render into. A third measured no gain per device (one 1080p frame is 8 MB).
    /// </summary>
    private const int PoolBuffers = 2;

    /// <summary>Title prefix of VIZEN's test-pattern windows, whose frames carry a frame-number marker.</summary>
    public const string TestPatternTitlePrefix = "VIZEN Test Pattern";

    private CaptureDevice _device;
    private int _deviceLostRaised;
    private readonly bool _hideBorder;
    private readonly object _gate = new();
    private readonly ISourceWindowHost? _host;
    private readonly string? _appKey;
    private readonly string _fallbackTitle;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private int _tracedFrames;
    private IntPtr _staging;
    private uint _stagingWidth;
    private uint _stagingHeight;

    // GPU path: frames go into a GPU texture ring; native presenters (overlay windows) draw from it on this capture's
    // thread. Falls back to the CPU path for good on any failure.
    private GpuFrameRing? _ring;
    private volatile bool _useGpu;
    private string? _gpuProblem;
    private readonly object _presenterGate = new();
    private NativeVideoPresenter[] _presenters = [];
    private long _lastCpuFrameAt;
    private bool _cpuBuffersTrimmed;

    internal WindowCapture(CaptureDevice device, IntPtr hwnd, double maxFps, bool hideBorder,
        ISourceWindowHost? host, string? appKey, string title, bool useGpu = true)
        : base(hwnd, maxFps)
    {
        _device = device;
        _useGpu = useGpu;
        _hideBorder = hideBorder;
        _host = host;
        _appKey = string.IsNullOrEmpty(appKey) ? null : appKey;
        _fallbackTitle = title;
        Handle = hwnd;
        HasFrameMarker = title.StartsWith(TestPatternTitlePrefix, StringComparison.Ordinal);
        IsSourceMinimized = NativeMethods.IsIconic(hwnd);
        if (_host is not null)
        {
            _host.KeepActiveChanged += OnKeepActiveChanged;
        }
    }

    public IntPtr Handle { get; }

    /// <summary>The Direct3D device this capture owns.</summary>
    internal CaptureDevice Device => _device;

    /// <summary>Raised on the UI thread when the GPU device was lost (driver reset, TDR, adapter removed).</summary>
    internal event EventHandler<CaptureDevice>? DeviceLost;

    /// <summary>Restarts this capture on a new device after a device loss. Consumers keep their lease.</summary>
    internal void Rebind(CaptureDevice device)
    {
        if (IsDisposed)
        {
            return;
        }

        ReleaseResources();
        _device = device;
        Interlocked.Exchange(ref _deviceLostRaised, 0);
        if (_host is not null)
        {
            _host.KeepActiveChanged += OnKeepActiveChanged;
        }

        Start();
    }

    public override bool SupportsWindowActions => true;

    public override bool KeepSourceActive
    {
        get => _host is not null && _appKey is not null && _host.IsKeepActive(_appKey);
        set
        {
            if (_host is null || _appKey is null || value == KeepSourceActive)
            {
                return;
            }

            _host.SetKeepActive(_appKey, CurrentTitle, value);
            if (value && IsUsable)
            {
                // Takes effect immediately for a source that is already minimized.
                IsSourceMinimized = CheckSourceMinimized();
            }
        }
    }

    private string CurrentTitle
    {
        get
        {
            var length = NativeMethods.GetWindowTextLength(Handle);
            if (length <= 0)
            {
                return _fallbackTitle;
            }

            var text = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(Handle, text, text.Capacity);
            return text.ToString();
        }
    }

    /// <summary>Size of the source window as reported by the capture item (physical pixels).</summary>
    public override SizeInt32 SourceSize => _item?.Size ?? base.SourceSize;

    internal void Start()
    {
        try
        {
            var itemPtr = D3D11Interop.CreateCaptureItemForWindow(Handle);
            _item = GraphicsCaptureItem.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            _item.Closed += OnItemClosed;

            _poolSize = _item.Size;
            if (_useGpu)
            {
                try
                {
                    _ring = new GpuFrameRing(_device);
                }
                catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
                {
                    _useGpu = false;
                    _gpuProblem = ex.Message;
                }
            }

            MemTrace.Mark($"capture: ring ({_poolSize.Width}x{_poolSize.Height})");
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device.WinRTDevice, PixelFormat, PoolBuffers, _poolSize);
            _framePool.FrameArrived += OnFrameArrived;
            MemTrace.Mark("capture: frame pool");

            _session = _framePool.CreateCaptureSession(_item);
            ConfigureSession(_session);
            _session.StartCapture();
            MemTrace.Mark("capture: session started");
        }
        catch (Exception ex)
        {
            Fail(DescribeStartError(ex));
            ReleaseResources();
        }
    }

    // Minimized windows are not rendered by Windows, so no frames arrive; say so instead of "idle".
    // With "Keep source active" the window is put back behind the active window instead (covered
    // windows keep rendering), preserving its size, position and maximized state.
    protected override bool CheckSourceMinimized()
    {
        if (!NativeMethods.IsIconic(Handle))
        {
            return false;
        }

        if (KeepSourceActive && SourceWindowActions.RestoreBehindActive(Handle))
        {
            _host?.ReportRestored(CurrentTitle, automatic: true);
            return false;
        }

        if (!IsSourceMinimized && Status == CaptureStatus.Live)
        {
            _host?.ReportMinimized(CurrentTitle);
        }

        return true;
    }

    protected override void RestoreSource()
    {
        if (SourceWindowActions.RestoreBehindActive(Handle))
        {
            _host?.ReportRestored(CurrentTitle, automatic: false);
            IsSourceMinimized = false;
        }
    }

    protected override void ShowSource()
    {
        var shown = SourceWindowActions.ShowAndActivate(Handle);
        _host?.ReportShown(CurrentTitle, shown);
        IsSourceMinimized = NativeMethods.IsIconic(Handle);
    }

    private void OnKeepActiveChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(KeepSourceActive));

    private void ConfigureSession(GraphicsCaptureSession session)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
            {
                session.IsCursorCaptureEnabled = false;
            }

            if (_hideBorder && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
                && ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
            {
                session.IsBorderRequired = false;
            }

            // Windows 11 24H2+ delivers at most one frame per 16 ms by default (measured): about 62 fps, so a 120/144 Hz
            // source would be capped before VIZEN sees it. VIZEN applies its own frame-rate caps, so let Windows deliver
            // every frame. VIZEN_WGC_INTERVAL=default keeps Windows' value for comparison.
            if (Environment.GetEnvironmentVariable("VIZEN_WGC_INTERVAL") != "default")
            {
                D3D11Interop.TrySetMinUpdateInterval(session, TimeSpan.FromMilliseconds(1));
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            // Optional cosmetics; capture still works with the default border/cursor.
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Latency is measured from the moment Windows hands the frame to VIZEN until it is displayed.
        var receivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        var handle = new FrameHandle(frame);
        long gpuWait = 0;
        try
        {
            lock (_gate)
            {
                if (IsDisposed)
                {
                    return;
                }

                var contentSize = frame.ContentSize;
                if (contentSize.Width <= 0 || contentSize.Height <= 0)
                {
                    return;
                }

                if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
                {
                    // Source was resized: future frames use the new size. This frame's texture still has
                    // the old size, so only its content region is copied below.
                    _poolSize = contentSize;
                    ReleaseHeldFrame();
                    sender.Recreate(_device.WinRTDevice, PixelFormat, PoolBuffers, _poolSize);
                }

                if (!TryBeginFrame())
                {
                    return;
                }

                try
                {
                    gpuWait = CopyFrame(handle, contentSize, receivedAt);
                }
            catch (COMException ex) when (CaptureDevice.IsDeviceLost(ex.HResult))
            {
                // The GPU device is gone: ask the service (once) to move every capture to a new device.
                if (Interlocked.Exchange(ref _deviceLostRaised, 1) == 0)
                {
                    var lost = _device;
                    Dispatcher.BeginInvoke(() => DeviceLost?.Invoke(this, lost));
                }
            }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    // Transient failures drop a frame; the next one may succeed.
                }
            }
        }
        finally
        {
            handle.Release();
            ReportCaptureWork(System.Diagnostics.Stopwatch.GetTimestamp() - receivedAt, gpuWait);
        }
    }

    /// <summary>
    /// A captured frame that is given back to Windows exactly once: as early as possible, or — when the GPU ring keeps it
    /// for the native presenters — when the ring lets go of it.
    /// </summary>
    private sealed class FrameHandle(Direct3D11CaptureFrame frame)
    {
        private bool _released;
        private bool _heldByRing;

        public Direct3D11CaptureFrame Frame { get; } = frame;

        /// <summary>From now on only the ring releases the frame (through the returned callback).</summary>
        public Action HandToRing()
        {
            _heldByRing = true;
            return () =>
            {
                _heldByRing = false;
                Release();
            };
        }

        public void Release()
        {
            if (!_released && !_heldByRing)
            {
                _released = true;
                Frame.Dispose();
            }
        }
    }

    /// <summary>Gives the frame the ring holds back to Windows (pool recreated or capture stopping).</summary>
    private void ReleaseHeldFrame()
    {
        if (_ring is { } ring)
        {
            lock (_device.ContextLock)
            {
                ring.ReleaseLatest();
            }
        }
    }

    /// <summary>Copies one frame to the CPU. Returns the ticks spent waiting for the GPU copy.</summary>
    private long CopyFrame(FrameHandle handle, SizeInt32 contentSize, long sourceTimestamp)
    {
        // Dispose the surface wrapper right away: left to the garbage collector it keeps the frame's GPU surface alive.
        using var surface = handle.Frame.Surface;
        var surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        IntPtr texture;
        try
        {
            texture = D3D11Interop.GetTextureFromSurface(surfacePtr);
        }
        finally
        {
            Marshal.Release(surfacePtr);
        }

        try
        {
            var desc = D3D11Interop.GetDesc(texture);
            var presenters = _presenters;
            var ring = _useGpu ? _ring : null;
            var lockRequested = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_device.ContextLock)
            {
                if (ring is not null)
                {
                    // GPU path: the ring keeps this frame (no copy) and every native presenter draws and presents it
                    // right here — no system memory, no UI thread.
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var deviceWait = started - lockRequested; // time spent waiting for other sources on the shared device
                    var marker = ring.Hold(texture, desc, contentSize, handle.HandToRing(), HasFrameMarker && presenters.Length > 0);
                    ReportNativeSize(Math.Min(contentSize.Width, (int)desc.Width), Math.Min(contentSize.Height, (int)desc.Height));
                    if (MemTrace.Enabled && _tracedFrames < 3)
                    {
                        _tracedFrames++;
                        MemTrace.Mark($"capture: frame {_tracedFrames} ({desc.Width}x{desc.Height}, {presenters.Length} presenters)");
                    }
                    if (presenters.Length > 0)
                    {
                        long presentTicks = 0;
                        var presented = false;
                        foreach (var presenter in presenters)
                        {
                            var ticks = presenter.Render();
                            if (ticks >= 0)
                            {
                                presented = true;
                                presentTicks += ticks;
                            }
                        }

                        ReportNativeFrame(System.Diagnostics.Stopwatch.GetTimestamp() - started, sourceTimestamp, presentTicks, presented, marker);

                        // WPF consumers (cards, previews) only need a CPU copy at their own, usually lower, rate.
                        if (!CpuFrameDue(sourceTimestamp))
                        {
                            // Nobody has needed a CPU copy for 5 s: give back the CPU buffers and the staging texture.
                            if (!_cpuBuffersTrimmed && sourceTimestamp - _lastCpuFrameAt > System.Diagnostics.Stopwatch.Frequency * 5)
                            {
                                ReleaseStagingTexture();
                                TrimCpuBuffers();
                                _cpuBuffersTrimmed = true;
                            }

                            handle.Release();
                            return deviceWait;
                        }

                        _cpuBuffersTrimmed = false;
                    }
                }

                EnsureStagingTexture(desc.Width, desc.Height);
                D3D11Interop.CopyResource(_device.Context, _staging, texture);

                var issued = System.Diagnostics.Stopwatch.GetTimestamp();
                var mapResult = D3D11Interop.Map(_device.Context, _staging, out var mapped);
                var gpuWait = System.Diagnostics.Stopwatch.GetTimestamp() - issued;
                if (mapResult < 0)
                {
                    if (CaptureDevice.IsDeviceLost(mapResult))
                    {
                        throw new COMException("Device lost while reading a frame.", mapResult);
                    }

                    return gpuWait;
                }

                // The GPU copy is complete: give the frame back now so Windows can render the next one while the
                // (larger) CPU copy below runs.
                handle.Release();
                try
                {
                    var width = Math.Min(contentSize.Width, (int)desc.Width);
                    var height = Math.Min(contentSize.Height, (int)desc.Height);
                    WriteFrame(width, height, (int)mapped.RowPitch, mapped.pData, sourceTimestamp);
                }
                finally
                {
                    D3D11Interop.Unmap(_device.Context, _staging);
                }

                return gpuWait;
            }
        }
        finally
        {
            Marshal.Release(texture);
        }
    }

    /// <summary>True when this capture can feed native presenters (GPU path available).</summary>
    internal bool SupportsNativePresentation => _useGpu && _ring is not null && !IsDisposed;

    /// <summary>Raised on the UI thread when native presenters were dropped (device recovery): attach again.</summary>
    internal event EventHandler? NativePresentersReset;

    /// <summary>
    /// UI thread: starts showing this capture in a native child window (physical pixel size). Null when the GPU path
    /// is not available or the swap chain could not be created; the caller then keeps the WPF image.
    /// </summary>
    internal NativeVideoPresenter? AttachPresenter(IntPtr hwnd, int width, int height, Rect viewbox, bool flipModel)
    {
        var ring = _ring;
        if (!SupportsNativePresentation || ring is null)
        {
            return null;
        }

        try
        {
            var presenter = new NativeVideoPresenter(ring, hwnd, width, height, flipModel) { Viewbox = viewbox };
            lock (_presenterGate)
            {
                _presenters = [.. _presenters, presenter];
                NativePresenting = true;
            }

            MemTrace.Mark($"presenter: swap chain {width}x{height}");
            presenter.Render(); // the last frame right away (a static source may not send a new one)
            MemTrace.Mark("presenter: first present");
            OnPropertyChanged(nameof(PipelineDescription));
            return presenter;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            _gpuProblem = ex.Message;
            return null;
        }
    }

    /// <summary>UI thread: stops and releases a presenter from <see cref="AttachPresenter"/>.</summary>
    internal void DetachPresenter(NativeVideoPresenter presenter)
    {
        lock (_presenterGate)
        {
            _presenters = [.. _presenters.Where(p => !ReferenceEquals(p, presenter))];
            NativePresenting = _presenters.Length > 0;
        }

        presenter.Dispose();
        OnPropertyChanged(nameof(PipelineDescription));
    }

    /// <summary>With native presenters, the CPU copy for WPF only happens at <see cref="LiveCapture.CpuFrameMaxFps"/>.</summary>
    private bool CpuFrameDue(long now)
    {
        var fps = CpuFrameMaxFps;
        if (fps < 0)
        {
            return false;
        }

        if (fps == 0 || now - _lastCpuFrameAt >= System.Diagnostics.Stopwatch.Frequency / fps)
        {
            _lastCpuFrameAt = now;
            return true;
        }

        return false;
    }

    public override string PipelineDescription
    {
        get
        {
            var presenters = _presenters;
            if (_useGpu && _ring is not null && presenters.Length > 0)
            {
                var cpu = CpuFrameMaxFps > 0 ? $"{CpuFrameMaxFps:0} fps" : "every frame";
                return $"GPU texture → native swap chain on the capture thread ({presenters.Length} window(s), {presenters[0].SwapEffect}); "
                    + $"no CPU copy for overlays; in-app previews: CPU copy at {cpu}";
            }

            return _gpuProblem is null
                ? "GPU→CPU copy, triple buffer, WPF WriteableBitmap (latest frame only)"
                : $"GPU→CPU copy, triple buffer, WPF WriteableBitmap (GPU path unavailable: {_gpuProblem})";
        }
    }

    private void EnsureStagingTexture(uint width, uint height)
    {
        if (_staging != IntPtr.Zero && _stagingWidth == width && _stagingHeight == height)
        {
            return;
        }

        ReleaseStagingTexture();

        var desc = new D3D11Interop.Texture2DDesc
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = D3D11Interop.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11Interop.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11Interop.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };

        Marshal.ThrowExceptionForHR(D3D11Interop.CreateTexture2D(_device.Device, ref desc, out _staging));
        _stagingWidth = width;
        _stagingHeight = height;
    }

    private void ReleaseStagingTexture()
    {
        if (_staging != IntPtr.Zero)
        {
            Marshal.Release(_staging);
            _staging = IntPtr.Zero;
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            ReleaseResources();
            MarkSourceClosed();
        });
    }

    private static string DescribeStartError(Exception ex) => ex switch
    {
        ArgumentException => Loc.T("Capture.CannotCapture"),
        UnauthorizedAccessException => Loc.T("Capture.AccessDenied"),
        _ => Loc.F("Capture.StartFailed", ex.Message),
    };

    protected override void ReleaseResources()
    {
        if (_host is not null)
        {
            _host.KeepActiveChanged -= OnKeepActiveChanged;
        }

        lock (_gate)
        {
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }

            ReleaseHeldFrame();
            _session?.Dispose();
            _session = null;
            _framePool?.Dispose();
            _framePool = null;

            if (_item is not null)
            {
                _item.Closed -= OnItemClosed;
                _item = null;
            }

            NativeVideoPresenter[] dropped;
            lock (_presenterGate)
            {
                dropped = _presenters;
                _presenters = [];
                NativePresenting = false;
            }

            foreach (var presenter in dropped)
            {
                presenter.Dispose();
            }

            lock (_device.ContextLock)
            {
                _ring?.Dispose();
                _ring = null;
                ReleaseStagingTexture();
            }

            if (dropped.Length > 0 && !IsDisposed)
            {
                // Device recovery: the overlay windows attach new presenters on the new device.
                Dispatcher.BeginInvoke(() => NativePresentersReset?.Invoke(this, EventArgs.Empty));
            }

            if (IsDisposed)
            {
                // The capture owns its device; Rebind() replaces it without coming here while alive.
                _device.Dispose();
            }
        }
    }
}
