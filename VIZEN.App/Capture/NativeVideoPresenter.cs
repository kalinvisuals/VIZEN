using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace VIZEN.App.Capture;

/// <summary>
/// Shows one capture in a native child window through a DXGI swap chain, like OBS previews and projectors:
/// the capture thread draws the newest GPU frame and presents it directly. The WPF UI thread and WPF's
/// composition are not involved in showing frames. Created and resized on the UI thread; rendered on the capture
/// thread; every device-context use is under the capture device's ContextLock.
/// </summary>
internal sealed unsafe class NativeVideoPresenter : IDisposable
{
    private readonly GpuFrameRing _ring;
    private readonly IntPtr _hwnd;
    private IntPtr _swapChain;
    private IntPtr _backBuffer;
    private IntPtr _target;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <param name="flipModel">
    /// False for windows inside a layered window (VIZEN overlays, for opacity): Windows composes flip-model swap chains
    /// there at the wrong size, so the blt model is used.
    /// </param>
    public NativeVideoPresenter(GpuFrameRing ring, IntPtr hwnd, int width, int height, bool flipModel)
    {
        _ring = ring;
        _hwnd = hwnd;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        lock (ring.Device.ContextLock)
        {
            var factory = D3D11Interop.GetFactory2(ring.Device.Device);
            try
            {
                // Flip model (what OBS uses on Windows 10+) where possible; the classic blt model otherwise.
                var hr = flipModel ? Create(factory, D3D11Interop.DXGI_SWAP_EFFECT_FLIP_DISCARD, 2) : -1;
                if (hr < 0)
                {
                    hr = Create(factory, D3D11Interop.DXGI_SWAP_EFFECT_DISCARD, 1);
                    SwapEffect = "blt (DISCARD)";
                }
                else
                {
                    SwapEffect = "flip (FLIP_DISCARD)";
                }

                Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                Marshal.Release(factory);
            }

            CreateTarget();
        }
    }

    /// <summary>The swap-chain model in use (for diagnostics).</summary>
    public string SwapEffect { get; } = "";

    /// <summary>Normalized part of the frame to show ((0,0,1,1) = whole window). Set from the UI thread.</summary>
    public Rect Viewbox { get; set; } = new(0, 0, 1, 1);

    /// <summary>Colour behind letterboxing, matching the overlay window background.</summary>
    public (float R, float G, float B) Background { get; set; } = (0.02f, 0.027f, 0.043f);

    private int Create(IntPtr factory, uint swapEffect, uint buffers)
    {
        var desc = new D3D11Interop.SwapChainDesc1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = D3D11Interop.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            BufferUsage = D3D11Interop.DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount = buffers,
            Scaling = 0, // STRETCH
            SwapEffect = swapEffect,
        };
        return D3D11Interop.CreateSwapChainForHwnd(factory, _ring.Device.Device, _hwnd, ref desc, out _swapChain);
    }

    private void CreateTarget()
    {
        Marshal.ThrowExceptionForHR(D3D11Interop.GetBackBuffer(_swapChain, out _backBuffer));
        Marshal.ThrowExceptionForHR(D3D11Interop.CreateRenderTargetView(_ring.Device.Device, _backBuffer, out _target));
    }

    /// <summary>
    /// Draws the newest frame and presents it (capture thread after each new frame, or UI thread after a resize).
    /// Returns the ticks spent, or -1 when nothing was presented.
    /// </summary>
    public long Render()
    {
        var start = Stopwatch.GetTimestamp();
        lock (_ring.Device.ContextLock)
        {
            if (_disposed || _target == IntPtr.Zero)
            {
                return -1;
            }

            var drawn = _ring.Draw(_target, _width, _height, Viewbox, Background);

            // Sync interval 0: with the flip model an unseen frame is simply replaced. (DO_NOT_WAIT was measured: it
            // skipped up to 63 % of the frames of some windows, so a present may wait briefly instead.)
            var hr = D3D11Interop.Present(_swapChain, 0);
            if (hr < 0 && CaptureDevice.IsDeviceLost(hr))
            {
                throw new COMException("Device lost while presenting.", hr);
            }

            return drawn ? Stopwatch.GetTimestamp() - start : -1;
        }
    }

    /// <summary>UI thread: the window was resized (physical pixels). Resizes the buffers and redraws the last frame.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        lock (_ring.Device.ContextLock)
        {
            if (_disposed || (width == _width && height == _height))
            {
                return;
            }

            GpuFrameRing.Release(ref _target);
            GpuFrameRing.Release(ref _backBuffer);
            Marshal.ThrowExceptionForHR(D3D11Interop.ResizeBuffers(_swapChain, (uint)width, (uint)height));
            _width = width;
            _height = height;
            CreateTarget();
        }

        Render();
    }

    /// <summary>UI thread.</summary>
    public void Dispose()
    {
        lock (_ring.Device.ContextLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            GpuFrameRing.Release(ref _target);
            GpuFrameRing.Release(ref _backBuffer);
            GpuFrameRing.Release(ref _swapChain);
        }
    }
}
