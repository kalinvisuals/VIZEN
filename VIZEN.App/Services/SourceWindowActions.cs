using System.Runtime.InteropServices;
using static VIZEN.App.Interop.NativeMethods;

namespace VIZEN.App.Services;

/// <summary>
/// Documented Win32 show/z-order calls on a source window. Windows does not render minimized windows,
/// so they produce no capture frames; windows that are merely covered by other windows keep rendering.
/// These helpers turn the first case into the second without stealing focus.
/// </summary>
internal static class SourceWindowActions
{
    private const long WS_EX_TOPMOST = 0x00000008L;

    /// <summary>
    /// Restores a minimized window to its previous size, position and maximized state without activating
    /// it, then places it directly behind the active window, so whatever the user is doing stays on top.
    /// Returns false when the window is gone or was not minimized.
    /// </summary>
    public static bool RestoreBehindActive(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || !IsIconic(hwnd))
        {
            return false;
        }

        var foreground = GetForegroundWindow();
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            return false;
        }

        if ((placement.flags & WPF_RESTORETOMAXIMIZED) != 0)
        {
            // There is no "maximize without activating" command; restore inactive, then re-apply the
            // saved placement as maximized, which keeps the original normal rectangle for later.
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            placement.showCmd = SW_SHOWMAXIMIZED;
            placement.flags &= ~WPF_RESTORETOMAXIMIZED;
            SetWindowPlacement(hwnd, ref placement);
        }
        else
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        }

        PlaceBehind(hwnd, foreground);
        return !IsIconic(hwnd);
    }

    /// <summary>Restores (if needed) and activates the window — only on an explicit user request.</summary>
    public static bool ShowAndActivate(IntPtr hwnd)
    {
        if (!IsWindow(hwnd))
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        return SetForegroundWindow(hwnd);
    }

    private static void PlaceBehind(IntPtr hwnd, IntPtr foreground)
    {
        // Inserting after a topmost window would make the source topmost too; leave the z-order alone then.
        if (foreground == IntPtr.Zero || foreground == hwnd
            || (GetWindowLongPtr(foreground, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
        {
            return;
        }

        SetWindowPos(hwnd, foreground, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
}
