using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VIZEN.App.Interop;

namespace VIZEN.App.Views;

/// <summary>
/// Win32 styling shared by every always-on-top layer window (capture overlays, media layers):
/// tool window (hidden from Alt+Tab and from VIZEN's source list), layered, and click-through via
/// WS_EX_TRANSPARENT so mouse input really reaches the window underneath.
/// </summary>
internal sealed class TopmostLayerStyle
{
    private readonly Window _window;
    private readonly Func<bool> _isClickThrough;
    private readonly bool _perPixelAlpha;
    private IntPtr _hwnd;

    /// <param name="perPixelAlpha">
    /// True for windows with AllowsTransparency (WPF draws them with per-pixel alpha, so opacity must go through
    /// <see cref="Window.Opacity"/>); false for opaque windows, whose opacity is set on the layered window.
    /// </param>
    public TopmostLayerStyle(Window window, Func<bool> isClickThrough, bool perPixelAlpha)
    {
        _window = window;
        _isClickThrough = isClickThrough;
        _perPixelAlpha = perPixelAlpha;
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Call from <see cref="Window.SourceInitialized"/>.</summary>
    /// <param name="systemFrame">
    /// True: Windows 11 rounded corners and its thin window outline (capture overlays). False: no corners and no
    /// outline at all — for logos and GIFs, where only the image itself may be visible.
    /// </param>
    public void Attach(bool systemFrame = true)
    {
        _hwnd = new WindowInteropHelper(_window).Handle;

        // WPF rewrites the extended style when it updates window state and would drop WS_EX_LAYERED,
        // so enforce the required bits on every style change.
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        _systemFrame = systemFrame;
        ApplyFrame();
        if (!systemFrame)
        {
            // WPF and DWM may restore their defaults when the window is first shown: apply again then.
            _window.ContentRendered += (_, _) => ApplyFrame();
        }

        ApplyExtendedStyle();
        if (_keepOnTop)
        {
            TopmostKeeper.Register(_hwnd, Band);
        }

        _window.Closed += (_, _) => TopmostKeeper.Unregister(_hwnd);
    }

    private bool _systemFrame = true;
    private bool _keepOnTop = true;

    /// <summary>
    /// Stacking band among VIZEN's topmost windows (set before <see cref="Attach"/>): higher bands are kept above lower
    /// ones. 0 = overlays, notes and media layers; Smart Draw uses 1 for its drawing surface and 2 for its toolbar.
    /// </summary>
    public int Band { get; init; }

    /// <summary>Never activated by a click (Smart Draw toolbar): the game or app underneath keeps the keyboard focus.</summary>
    public bool NoActivate { get; init; }

    /// <summary>
    /// Whether the window stays above other always-on-top windows (borderless games). On by default; Smart Notes can
    /// turn "always on top" off, and the window then behaves like a normal window.
    /// </summary>
    public void SetKeepOnTop(bool keep)
    {
        _keepOnTop = keep;
        _window.Topmost = keep;
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (keep)
        {
            TopmostKeeper.Register(_hwnd, Band);
        }
        else
        {
            TopmostKeeper.Unregister(_hwnd);
        }
    }

    private void ApplyFrame()
    {
        var corner = _systemFrame ? NativeMethods.DWMWCP_ROUND : 1; // 1 = DWMWCP_DONOTROUND
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        if (_systemFrame)
        {
            return;
        }

        // No Windows 11 outline, no rounded corners, no frame drawing at all: only the image is visible.
        var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        var disabled = 1; // DWMNCRP_DISABLED
        NativeMethods.DwmSetWindowAttribute(_hwnd, 2 /* DWMWA_NCRENDERING_POLICY */, ref disabled, sizeof(int));
    }

    public void ApplyExtendedStyle()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, WithRequiredExStyle(exStyle));
    }

    /// <summary>Whole-window opacity, 0..1.</summary>
    public void ApplyOpacity(double opacity)
    {
        if (_perPixelAlpha)
        {
            _window.Opacity = opacity;
            return;
        }

        if (_hwnd != IntPtr.Zero)
        {
            var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_STYLECHANGING && (int)wParam.ToInt64() == NativeMethods.GWL_EXSTYLE)
        {
            var styles = Marshal.PtrToStructure<NativeMethods.STYLESTRUCT>(lParam);
            styles.styleNew = (uint)WithRequiredExStyle(styles.styleNew);
            Marshal.StructureToPtr(styles, lParam, false);

            // WPF's HwndTarget would otherwise clear WS_EX_LAYERED on windows without AllowsTransparency.
            handled = true;
        }

        return IntPtr.Zero;
    }

    private long WithRequiredExStyle(long exStyle)
    {
        const long clickThroughFlags = NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
        exStyle = _isClickThrough() ? exStyle | clickThroughFlags : exStyle & ~clickThroughFlags;
        return NoActivate ? exStyle | NativeMethods.WS_EX_NOACTIVATE : exStyle;
    }
}

/// <summary>
/// Keeps VIZEN's always-on-top windows above other always-on-top windows. Borderless ("fullscreen windowed") games such
/// as Counter-Strike 2 make their own window topmost when they get the focus, which puts them above windows that became
/// topmost earlier. On every foreground change (and every 2 s as a fallback) the registered windows are moved back to
/// the top of the topmost band, without activating them. Plain window positioning: nothing is injected into the game.
/// Exclusive fullscreen cannot be drawn over this way: a window above an exclusive-fullscreen game makes Windows take the
/// screen back and minimize the game. So while Windows reports an exclusive-fullscreen Direct3D app nothing is raised,
/// and as a fallback a full-screen window that minimizes right after a raise is left alone from then on.
/// <see cref="ExclusiveFullscreenBlocked"/> tells the shell, which asks the user to switch the game to borderless.
/// </summary>
internal static class TopmostKeeper
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly HashSet<IntPtr> Windows = [];
    private static readonly Dictionary<IntPtr, int> Bands = [];
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private static readonly TimeSpan MinimizeAfterRaise = TimeSpan.FromSeconds(2);
    private static readonly WinEventProc Callback = (_, eventType, hwnd, _, _, _, _) =>
    {
        if (eventType == EVENT_SYSTEM_MINIMIZESTART)
        {
            OnMinimized(hwnd);
        }
        else
        {
            RaiseAll();
        }
    };

    /// <summary>Full-screen windows that minimized right after VIZEN raised its overlays (exclusive fullscreen).</summary>
    private static readonly HashSet<IntPtr> ExclusiveWindows = [];
    private static IntPtr _hook;
    private static IntPtr _minimizeHook;
    private static bool _exclusiveReported;
    private static IntPtr _raisedOver;
    private static DateTime _raisedAt;

    /// <summary>
    /// Raised (UI thread) when overlays cannot be shown because a game runs in exclusive fullscreen; once per
    /// exclusive-fullscreen period.
    /// </summary>
    public static event EventHandler? ExclusiveFullscreenBlocked;
    private static System.Windows.Threading.DispatcherTimer? _timer;

    public static void Register(IntPtr hwnd, int band = 0)
    {
        if (hwnd != IntPtr.Zero)
        {
            Bands[hwnd] = band;
        }

        if (hwnd == IntPtr.Zero || !Windows.Add(hwnd) || _hook != IntPtr.Zero)
        {
            return;
        }

        // Out-of-context hook: called on this (UI) thread through its message loop.
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, Callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        _minimizeHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, Callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        _timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => RaiseAll(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    public static void Unregister(IntPtr hwnd)
    {
        Bands.Remove(hwnd);
        if (!Windows.Remove(hwnd) || Windows.Count > 0)
        {
            return;
        }

        _timer?.Stop();
        _timer = null;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        if (_minimizeHook != IntPtr.Zero)
        {
            UnhookWinEvent(_minimizeHook);
            _minimizeHook = IntPtr.Zero;
        }
    }

    /// <summary>Restacks VIZEN's topmost windows now (e.g. right after Smart Draw opened its windows).</summary>
    public static void RaiseNow() => RaiseAll();

    private static void RaiseAll()
    {
        var foreground = GetForegroundWindow();
        ExclusiveWindows.RemoveWhere(hwnd => !IsWindow(hwnd));
        if (IsExclusiveFullscreen() || ExclusiveWindows.Contains(foreground))
        {
            // Raising now would minimize the game.
            ReportExclusive();
            return;
        }

        _exclusiveReported = false;
        // Remembered only when an overlay really moves above a full-screen window: a raise that changes nothing (the
        // overlays already on top, e.g. on the 2 s timer) cannot be what minimized it.
        _raisedOver = CoversItsMonitor(foreground) && Windows.Any(hwnd => IsWindowVisible(hwnd) && IsAbove(foreground, hwnd))
            ? foreground : IntPtr.Zero;
        _raisedAt = DateTime.UtcNow;
        // Lower bands first: each SetWindowPos puts the window at the top, so the highest band ends up in front.
        foreach (var hwnd in Windows.OrderBy(h => Bands.GetValueOrDefault(h)).ToList())
        {
            // Hidden overlays ("Visible" off) stay hidden.
            if (IsWindowVisible(hwnd))
            {
                NativeMethods.SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
            }
        }
    }

    /// <summary>Fallback for exclusive-fullscreen games that Windows does not report as such.</summary>
    private static void OnMinimized(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && hwnd == _raisedOver && DateTime.UtcNow - _raisedAt <= MinimizeAfterRaise && ExclusiveWindows.Add(hwnd))
        {
            ReportExclusive();
        }
    }

    private static void ReportExclusive()
    {
        if (!_exclusiveReported)
        {
            _exclusiveReported = true;
            ExclusiveFullscreenBlocked?.Invoke(null, EventArgs.Empty);
        }
    }

    private static bool IsExclusiveFullscreen() =>
        SHQueryUserNotificationState(out var state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;

    private static bool CoversItsMonitor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || Windows.Contains(hwnd) || !GetWindowRect(hwnd, out var window))
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref info)
            && window.Left <= info.rcMonitor.Left && window.Top <= info.rcMonitor.Top
            && window.Right >= info.rcMonitor.Right && window.Bottom >= info.rcMonitor.Bottom;
    }

    /// <summary>True when <paramref name="upper"/> is above <paramref name="lower"/> in the Z order.</summary>
    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (var hwnd = GetWindow(upper, GW_HWNDNEXT); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
        {
            if (hwnd == lower)
            {
                return true;
            }
        }

        return false;
    }

    private const uint GW_HWNDNEXT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
