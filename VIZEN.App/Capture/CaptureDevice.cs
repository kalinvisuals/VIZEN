using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace VIZEN.App.Capture;

/// <summary>
/// Direct3D 11 device of a window capture (or shared by all of them with VIZEN_DEVICE=shared, like OBS).
/// The immediate context is not thread-safe, so every use must hold <see cref="ContextLock"/>.
/// Reference-counted: each holder calls <see cref="AddRef"/> and later <see cref="Dispose"/>.
/// </summary>
internal sealed class CaptureDevice : IDisposable
{
    private CaptureDevice(IntPtr device, IntPtr context, IDirect3DDevice winrtDevice)
    {
        Device = device;
        Context = context;
        WinRTDevice = winrtDevice;
    }

    public IntPtr Device { get; private set; }

    public IntPtr Context { get; private set; }

    public IDirect3DDevice WinRTDevice { get; }

    public object ContextLock { get; } = new();

    private int _references = 1;

    /// <summary>One more holder; it must call <see cref="Dispose"/> when done.</summary>
    public CaptureDevice AddRef()
    {
        Interlocked.Increment(ref _references);
        return this;
    }

    /// <summary>
    /// True for the DXGI errors that mean the GPU device is gone (driver update/crash, GPU reset, TDR, adapter
    /// removed). Capture can only continue on a newly created device.
    /// </summary>
    public static bool IsDeviceLost(int hresult) => unchecked((uint)hresult) is
        0x887A0005 // DXGI_ERROR_DEVICE_REMOVED
        or 0x887A0006 // DXGI_ERROR_DEVICE_HUNG
        or 0x887A0007 // DXGI_ERROR_DEVICE_RESET
        or 0x887A0020; // DXGI_ERROR_DRIVER_INTERNAL_ERROR

    /// <summary>
    /// VIZEN submits a copy-free draw and a present per frame, so the driver's own worker threads only add memory: measured on
    /// an integrated GPU with 4 sources, preventing them saved ~75 MB of RAM and some CPU without costing frames.
    /// VIZEN_DRIVER_THREADS=1 lets the driver use them again.
    /// </summary>
    private static readonly uint DriverThreadingFlag = Environment.GetEnvironmentVariable("VIZEN_DRIVER_THREADS") == "1"
        ? 0u : D3D11Interop.D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS;

    public static CaptureDevice Create()
    {
        var hr = D3D11Interop.D3D11CreateDevice(
            IntPtr.Zero, D3D11Interop.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11Interop.D3D11_CREATE_DEVICE_BGRA_SUPPORT | DriverThreadingFlag,
            IntPtr.Zero, 0, D3D11Interop.D3D11_SDK_VERSION, out var device, out _, out var context);

        if (hr < 0)
        {
            // No usable GPU (e.g. some VMs / remote sessions): fall back to the software rasterizer.
            hr = D3D11Interop.D3D11CreateDevice(
                IntPtr.Zero, D3D11Interop.D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11Interop.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11Interop.D3D11_SDK_VERSION, out device, out _, out context);
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            var iid = D3D11Interop.IID_IDXGIDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out var dxgiDevice));
            IntPtr inspectable;
            try
            {
                Marshal.ThrowExceptionForHR(D3D11Interop.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable));
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }

            var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
            return new CaptureDevice(device, context, winrtDevice);
        }
        catch
        {
            Marshal.Release(context);
            Marshal.Release(device);
            throw;
        }
    }

    /// <summary>Drops one reference; the device is released with the last one.</summary>
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) != 0)
        {
            return;
        }

        lock (ContextLock)
        {
            WinRTDevice.Dispose();
            if (Context != IntPtr.Zero)
            {
                // D3D11 destroys released objects lazily; unbind and flush so their GPU memory is freed now.
                D3D11Interop.ClearState(Context);
                D3D11Interop.Flush(Context);
                Marshal.Release(Context);
                Context = IntPtr.Zero;
            }

            if (Device != IntPtr.Zero)
            {
                Marshal.Release(Device);
                Device = IntPtr.Zero;
            }
        }
    }
}
