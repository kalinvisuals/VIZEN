using System.Runtime.InteropServices;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.Views;

/// <summary>The connected monitors from Win32: device name, bounds and work area in physical pixels, DPI scale.</summary>
public sealed class MonitorProvider : IMonitorProvider
{
    public IReadOnlyList<MonitorInfo> Current()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var scale = GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
                var m = info.rcMonitor;
                var w = info.rcWork;
                list.Add(new MonitorInfo(info.szDevice, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, scale, (info.dwFlags & 1) != 0)
                {
                    WorkLeft = w.Left, WorkTop = w.Top, WorkWidth = w.Right - w.Left, WorkHeight = w.Bottom - w.Top,
                });
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
