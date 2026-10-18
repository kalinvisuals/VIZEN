using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VIZEN.App.Interop;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Clean output window for OBS Window Capture. It is a normal top-level window (not a tool window, so OBS
/// lists it) with square corners and nothing but the content. All controls live in VIZEN's Studio page.
/// </summary>
public partial class OutputWindow : Window
{
    private const int DWMWCP_DONOTROUND = 1;

    private readonly OutputViewModel _output;

    public OutputWindow(OutputViewModel output)
    {
        InitializeComponent();
        _output = output;
        DataContext = output;

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => TrackBounds();
        LocationChanged += (_, _) => TrackBounds();
        output.SizeRequested += OnSizeRequested;
        output.BringToFrontRequested += OnBringToFrontRequested;
        output.Closed += OnOutputClosed;
        Closed += OnWindowClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Windows 11 rounds window corners; a capture source should be a plain rectangle.
        var hwnd = new WindowInteropHelper(this).Handle;
        var corner = DWMWCP_DONOTROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>
    /// Sets the client size from a physical-pixel size (what OBS will capture), limited to the screen size,
    /// and returns the size actually achieved.
    /// </summary>
    public Size SetPixelSize(Size pixels) => ApplyPixelSize(this, pixels);

    /// <summary>Shared by every OBS window: sizes <paramref name="window"/> in physical pixels, limited to the virtual screen.</summary>
    internal static Size ApplyPixelSize(Window window, Size pixels)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        window.Width = Math.Clamp(pixels.Width / dpi.DpiScaleX, window.MinWidth, Math.Max(window.MinWidth, SystemParameters.VirtualScreenWidth));
        window.Height = Math.Clamp(pixels.Height / dpi.DpiScaleY, window.MinHeight, Math.Max(window.MinHeight, SystemParameters.VirtualScreenHeight));
        window.UpdateLayout();
        return new Size(Math.Round(window.ActualWidth * dpi.DpiScaleX), Math.Round(window.ActualHeight * dpi.DpiScaleY));
    }

    private void OnSizeRequested(object? sender, OutputSizeRequest request) => request.Applied = SetPixelSize(request.Requested);

    private void OnBringToFrontRequested(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void TrackBounds()
    {
        if (WindowState != WindowState.Normal || ActualWidth <= 0)
        {
            return;
        }

        _output.Bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(this);
        _output.ReportPixelSize((int)Math.Round(ActualWidth * dpi.DpiScaleX), (int)Math.Round(ActualHeight * dpi.DpiScaleY));
    }

    private void OnContentMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnOutputClosed(object? sender, EventArgs e) => Close();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _output.SizeRequested -= OnSizeRequested;
        _output.BringToFrontRequested -= OnBringToFrontRequested;
        _output.Closed -= OnOutputClosed;
        _output.Close(); // no-op if the output initiated the close
    }
}
