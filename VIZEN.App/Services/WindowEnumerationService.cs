using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VIZEN.App.Interop;
using VIZEN.App.Models;

namespace VIZEN.App.Services;

public sealed record WindowEnumerationOptions(bool ExcludeOwnProcess, bool IncludeMinimized);

public sealed partial class WindowEnumerationService
{
    /// <summary>Title prefix of VIZEN's browser-source windows (listed as sources even though they are VIZEN's own).</summary>
    public const string BrowserSourceTitlePrefix = "VIZEN Browser · ";

    /// <summary>Process name shown for browser sources (and recognized as a browser by the classifier).</summary>
    public const string BrowserSourceProcessName = "VIZEN Browser";
}

/// <summary>
/// Enumerates visible top-level windows using EnumWindows, applying roughly the same rules
/// Windows uses for Alt+Tab (visible, not cloaked, not a tool window, unowned, has a title).
/// This is a read-only query; it does not capture pixels or interact with the windows.
/// Icons and process details are cached between calls so frequent auto-refresh stays cheap.
/// </summary>
public sealed partial class WindowEnumerationService
{
    private const string UwpHostProcess = "ApplicationFrameHost";

    private static readonly HashSet<string> IgnoredClassNames = new(StringComparer.Ordinal)
    {
        "Progman",          // Desktop ("Program Manager")
        "WorkerW",          // Desktop wallpaper host
        "Shell_TrayWnd",    // Taskbar
        "Shell_SecondaryTrayWnd",
    };

    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, ImageSource?> _iconCache = [];
    private readonly Dictionary<uint, (string Name, string? Path)> _processCache = [];

    /// <summary>Safe to call from any thread; concurrent calls are serialized.</summary>
    public IReadOnlyList<WindowSource> EnumerateWindows(WindowEnumerationOptions options)
    {
        lock (_gate)
        {
            return EnumerateCore(options);
        }
    }

    private List<WindowSource> EnumerateCore(WindowEnumerationOptions options)
    {
        var handles = new List<IntPtr>(256);
        NativeMethods.EnumWindowsProc callback = (hWnd, _) =>
        {
            handles.Add(hWnd);
            return true;
        };
        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        var ownPid = (uint)Environment.ProcessId;
        var results = new List<WindowSource>();
        var seenHandles = new HashSet<IntPtr>();
        var seenPids = new HashSet<uint>();
        var zOrder = 0;

        foreach (var hWnd in handles)
        {
            if (!IsAltTabCandidate(hWnd, out var title, out var className))
            {
                continue;
            }

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);

            // VIZEN's own browser-source windows are real capture sources; other VIZEN windows are not.
            var isBrowserSource = pid == ownPid && title.StartsWith(BrowserSourceTitlePrefix, StringComparison.Ordinal);
            if (options.ExcludeOwnProcess && pid == ownPid && !isBrowserSource)
            {
                continue;
            }

            if (!DemoMode.ShowsWindow(title))
            {
                continue;
            }

            var state = NativeMethods.IsIconic(hWnd)
                ? WindowDisplayState.Minimized
                : NativeMethods.IsZoomed(hWnd) ? WindowDisplayState.Maximized : WindowDisplayState.Normal;

            if (!options.IncludeMinimized && state == WindowDisplayState.Minimized)
            {
                continue;
            }

            var bounds = GetVisibleBounds(hWnd);
            if (state != WindowDisplayState.Minimized && (bounds.Width <= 0 || bounds.Height <= 0))
            {
                continue;
            }

            var process = GetProcessInfo(pid);

            // UWP apps (Settings, Camera, …) are hosted by ApplicationFrameHost; report the real app.
            if (string.Equals(process.Name, UwpHostProcess, StringComparison.OrdinalIgnoreCase))
            {
                var appPid = FindHostedAppProcess(hWnd, pid);
                if (appPid != pid)
                {
                    pid = appPid;
                    process = GetProcessInfo(pid);
                }
            }

            seenHandles.Add(hWnd);
            seenPids.Add(pid);

            if (!_iconCache.TryGetValue(hWnd, out var icon))
            {
                icon = TryGetIcon(hWnd);
                _iconCache[hWnd] = icon;
            }

            results.Add(new WindowSource
            {
                Handle = hWnd,
                Title = title,
                ClassName = className,
                ProcessId = pid,
                ProcessName = isBrowserSource ? BrowserSourceProcessName : process.Name,
                ExecutablePath = process.Path,
                IsBrowser = isBrowserSource || SourceClassifier.IsBrowser(process.Name),
                Category = SourceClassifier.Classify(isBrowserSource ? BrowserSourceProcessName : process.Name, title),
                X = bounds.Left,
                Y = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                State = state,
                Dpi = SafeGetDpi(hWnd),
                Icon = icon,
                ZOrder = zOrder++,
            });
        }

        // Forget windows and processes that no longer exist (handles and PIDs can be reused).
        foreach (var stale in _iconCache.Keys.Where(h => !seenHandles.Contains(h)).ToList())
        {
            _iconCache.Remove(stale);
        }

        foreach (var stale in _processCache.Keys.Where(p => !seenPids.Contains(p)).ToList())
        {
            _processCache.Remove(stale);
        }

        return results;
    }

    private (string Name, string? Path) GetProcessInfo(uint pid)
    {
        if (!_processCache.TryGetValue(pid, out var info))
        {
            info = (GetProcessName(pid), GetExecutablePath(pid));
            _processCache[pid] = info;
        }

        return info;
    }

    private static uint FindHostedAppProcess(IntPtr frameWindow, uint hostPid)
    {
        uint found = hostPid;
        NativeMethods.EnumWindowsProc callback = (child, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(child, out var childPid);
            if (childPid != hostPid)
            {
                found = childPid;
                return false;
            }

            return true;
        };
        NativeMethods.EnumChildWindows(frameWindow, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    private static bool IsAltTabCandidate(IntPtr hWnd, out string title, out string className)
    {
        title = string.Empty;
        className = string.Empty;

        if (!NativeMethods.IsWindowVisible(hWnd))
        {
            return false;
        }

        // Cloaked windows (e.g. suspended UWP apps, windows on other virtual desktops) are not on screen.
        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0)
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);
        var isAppWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && !isAppWindow)
        {
            return false;
        }

        if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero && !isAppWindow)
        {
            return false;
        }

        var length = NativeMethods.GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return false;
        }

        var titleBuilder = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
        title = titleBuilder.ToString().Trim();
        if (title.Length == 0)
        {
            return false;
        }

        var classBuilder = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
        className = classBuilder.ToString();

        return !IgnoredClassNames.Contains(className);
    }

    private static NativeMethods.RECT GetVisibleBounds(IntPtr hWnd)
    {
        // Extended frame bounds exclude the invisible resize borders Windows 10/11 add around windows.
        if (NativeMethods.DwmGetWindowAttribute(
                hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT rect,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0)
        {
            return rect;
        }

        NativeMethods.GetWindowRect(hWnd, out rect);
        return rect;
    }

    private static uint SafeGetDpi(IntPtr hWnd)
    {
        try
        {
            return NativeMethods.GetDpiForWindow(hWnd);
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "Unknown";
        }
    }

    private static string? GetExecutablePath(uint pid)
    {
        var hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Reads the window's own icon. Uses SendMessageTimeout with SMTO_ABORTIFHUNG so a hung
    /// application cannot stall enumeration. Returns a frozen, thread-agnostic bitmap.
    /// </summary>
    private static ImageSource? TryGetIcon(IntPtr hWnd)
    {
        var hIcon = QueryIcon(hWnd, NativeMethods.ICON_BIG);
        if (hIcon == IntPtr.Zero)
        {
            hIcon = QueryIcon(hWnd, NativeMethods.ICON_SMALL2);
        }

        if (hIcon == IntPtr.Zero)
        {
            hIcon = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCLP_HICON);
        }

        if (hIcon == IntPtr.Zero)
        {
            hIcon = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCLP_HICONSM);
        }

        if (hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var interop = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Copy into a plain BitmapSource so the result can be frozen and handed to the UI thread.
            var converted = new FormatConvertedBitmap(interop, PixelFormats.Pbgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            var bitmap = BitmapSource.Create(
                converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IntPtr QueryIcon(IntPtr hWnd, int iconType)
    {
        var ok = NativeMethods.SendMessageTimeout(
            hWnd, NativeMethods.WM_GETICON, new IntPtr(iconType), IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, 100, out var result);
        return ok == IntPtr.Zero ? IntPtr.Zero : result;
    }
}
