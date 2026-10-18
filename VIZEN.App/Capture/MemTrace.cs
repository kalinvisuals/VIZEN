using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VIZEN.App.Capture;

/// <summary>
/// Diagnostics only (VIZEN_MEMTRACE=&lt;file&gt;): logs the process's private bytes and its GPU memory usage
/// (DXGI QueryVideoMemoryInfo, local + non-local) at named steps, to see what each part of a capture costs.
/// </summary>
internal static unsafe class MemTrace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("VIZEN_MEMTRACE");
    private static readonly object Gate = new();
    private static IntPtr _adapter3;

    public static bool Enabled => Path is not null;

    public static void Mark(string step, IntPtr device = default)
    {
        if (Path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (_adapter3 == IntPtr.Zero && device != IntPtr.Zero)
                {
                    _adapter3 = GetAdapter3(device);
                }

                ulong local = 0, shared = 0;
                if (_adapter3 != IntPtr.Zero)
                {
                    local = Query(_adapter3, 0);
                    shared = Query(_adapter3, 1);
                }

                using var process = Process.GetCurrentProcess();
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {step,-34} private {process.PrivateMemorySize64 / 1048576.0,7:F1} MB  gpu local {local / 1048576.0,6:F1} MB  gpu shared {shared / 1048576.0,6:F1} MB  managed {GC.GetTotalMemory(false) / 1048576.0,6:F1} MB\n");
            }
        }
        catch (Exception ex) when (ex is IOException or COMException or UnauthorizedAccessException)
        {
        }
    }

    // IDXGIAdapter3::QueryVideoMemoryInfo (slot 14); group 0 = local, 1 = non-local
    private static ulong Query(IntPtr adapter3, int group)
    {
        var info = stackalloc ulong[4];
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, ulong*, int>)(*(IntPtr**)adapter3)[14])(adapter3, 0, group, info);
        return hr >= 0 ? info[1] : 0;
    }

    private static IntPtr GetAdapter3(IntPtr device)
    {
        var iidDevice = D3D11Interop.IID_IDXGIDevice;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iidDevice, out var dxgiDevice));
        try
        {
            IntPtr adapter;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)dxgiDevice)[7])(dxgiDevice, &adapter));
            try
            {
                var iid = new Guid("645967a4-1392-4310-a798-8053ce3e93fd"); // IDXGIAdapter3
                return Marshal.QueryInterface(adapter, in iid, out var adapter3) >= 0 ? adapter3 : IntPtr.Zero;
            }
            finally
            {
                Marshal.Release(adapter);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }
}
