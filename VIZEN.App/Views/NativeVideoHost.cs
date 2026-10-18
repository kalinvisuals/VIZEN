using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VIZEN.App.Views;

/// <summary>
/// A plain native child window that a swap chain can draw into, hosted inside a WPF window. It never takes the mouse
/// (WM_NCHITTEST → HTTRANSPARENT), so dragging, double-click and hover on the overlay keep working, and it never
/// paints anything itself, so only the swap chain's image is visible.
/// </summary>
internal sealed class NativeVideoHost : HwndHost
{
    private const string ClassName = "VIZEN.NativeVideo";
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const int WM_NCHITTEST = 0x0084, WM_ERASEBKGND = 0x0014, WM_PAINT = 0x000F, WM_SIZE = 0x0005;
    private const int HTTRANSPARENT = -1;

    private static readonly WndProcDelegate WndProcKeepAlive = StaticWndProc;
    private static bool _classRegistered;
    private static readonly Dictionary<IntPtr, NativeVideoHost> Hosts = [];

    private IntPtr _hwnd;

    /// <summary>Raised (UI thread) when the child window's size in physical pixels changes, and once after creation.</summary>
    public event Action<IntPtr, int, int>? PixelSizeChanged;

    public IntPtr ChildHandle => _hwnd;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        RegisterClassOnce();
        _hwnd = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not create the native video window (error " + Marshal.GetLastWin32Error() + ").");
        }

        Hosts[_hwnd] = this;
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Hosts.Remove(hwnd.Handle);
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    private static void RegisterClassOnce()
    {
        if (_classRegistered)
        {
            return;
        }

        var wc = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcKeepAlive),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
        {
            throw new InvalidOperationException("Could not register the native video window class.");
        }

        _classRegistered = true;
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                return HTTRANSPARENT;
            case WM_ERASEBKGND:
                return 1; // the swap chain covers the whole window
            case WM_PAINT:
                ValidateRect(hwnd, IntPtr.Zero);
                return IntPtr.Zero;
            case WM_SIZE:
                if (Hosts.TryGetValue(hwnd, out var host))
                {
                    var w = (int)(lParam.ToInt64() & 0xFFFF);
                    var h = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                    host.PixelSizeChanged?.Invoke(hwnd, w, h);
                }

                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
