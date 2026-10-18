using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VIZEN.App.Interop;
using VIZEN.App.ViewModels;

namespace VIZEN.App;

/// <summary>
/// Borderless shell window. Code-behind is limited to window-chrome concerns
/// (caption buttons, maximize bounds, DWM styling, responsive sidebar).
/// </summary>
public partial class MainWindow : Window
{
    private const double CompactSidebarThreshold = 1120;
    private bool? _wasNarrow;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateWindowStateVisuals();
        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// Keeps the default size inside the primary work area so the title bar is never placed off-screen
    /// on small or highly scaled displays (e.g. 1920×1080 at 150% is only 1280×720 DIPs).
    /// </summary>
    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, work.Width);
        MinHeight = Math.Min(MinHeight, work.Height);
        Width = Math.Max(MinWidth, Math.Min(Width, work.Width * 0.92));
        Height = Math.Max(MinHeight, Math.Min(Height, work.Height * 0.92));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Windows 11 rounded corners; system UI and outline follow the theme (see ApplyThemeFrame).
        // These calls are no-ops on Windows versions that don't support the attribute.
        var corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        ApplyThemeFrame();
        Services.ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => Services.ThemeManager.Changed -= OnThemeChanged;

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        UpdateWindowStateVisuals();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyThemeFrame();

    /// <summary>Dark or light system UI (resize borders, Alt+Tab frame) and an outline matching Brush.Border.</summary>
    private void ApplyThemeFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var dark = Services.ThemeManager.IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        if (TryFindResource("Brush.Border") is System.Windows.Media.SolidColorBrush border)
        {
            var c = border.Color;
            var colorRef = c.R | (c.G << 8) | (c.B << 16); // COLORREF is 0x00BBGGRR
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_GETMINMAXINFO)
        {
            ConstrainMaximizeToWorkArea(hwnd, lParam);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Without a standard frame, Windows maximizes over the taskbar. Clamp the maximized
    /// bounds to the work area of the monitor the window is on.
    /// </summary>
    private static void ConstrainMaximizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Width;
        mmi.ptMaxSize.Y = info.rcWork.Height;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void UpdateWindowStateVisuals()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxRestoreIcon.Data = (System.Windows.Media.Geometry)FindResource(maximized ? "Geo.CaptionRestore" : "Geo.CaptionMaximize");
        MaxRestoreButton.SetResourceReference(ToolTipProperty, maximized ? "Shell.Restore" : "Shell.Maximize");
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Auto-collapse the sidebar only when crossing the threshold, so a manual toggle sticks.
        var narrow = ActualWidth < CompactSidebarThreshold;
        if (_wasNarrow != narrow && DataContext is MainViewModel vm)
        {
            _wasNarrow = narrow;
            vm.IsSidebarCompact = narrow;
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    // The toast stays while the user reads it with the pointer on it.
    private void OnToastMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => (DataContext as ViewModels.MainViewModel)?.Toasts.SetHovered(true);

    private void OnToastMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => (DataContext as ViewModels.MainViewModel)?.Toasts.SetHovered(false);
}
