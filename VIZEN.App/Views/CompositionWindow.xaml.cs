using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VIZEN.App.Interop;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// One composition as a clean OBS window: the canvas scaled uniformly into the window, items stacked by their
/// order. In edit mode each item gets a frame, a move area and a resize handle (in canvas pixels).
/// </summary>
public partial class CompositionWindow : Window
{
    private const int DWMWCP_DONOTROUND = 1;

    private readonly CompositionViewModel _composition;

    public CompositionWindow(CompositionViewModel composition)
    {
        InitializeComponent();
        _composition = composition;
        DataContext = composition;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var corner = DWMWCP_DONOTROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        };
        SizeChanged += (_, _) => TrackBounds();
        LocationChanged += (_, _) => TrackBounds();
        composition.SizeRequested += OnSizeRequested;
        Closed += (_, _) => composition.SizeRequested -= OnSizeRequested;
    }

    private void OnSizeRequested(object? sender, OutputSizeRequest request) => request.Applied = OutputWindow.ApplyPixelSize(this, request.Requested);

    private void TrackBounds()
    {
        if (WindowState != WindowState.Normal || ActualWidth <= 0 || !IsLoaded)
        {
            return;
        }

        _composition.ReportWindowBounds(new Rect(Left, Top, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this);
        _composition.ReportPixelSize((int)Math.Round(ActualWidth * dpi.DpiScaleX), (int)Math.Round(ActualHeight * dpi.DpiScaleY));
    }

    private void OnBackgroundMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Thumb)
        {
            DragMove();
        }
    }

    // Thumb deltas are in the item's own coordinates, i.e. canvas pixels (the Viewbox scales them).
    private void OnMoveDelta(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CompositionItemViewModel item)
        {
            item.MoveBy(e.HorizontalChange, e.VerticalChange);
        }
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CompositionItemViewModel item)
        {
            item.ResizeBy(e.HorizontalChange, e.VerticalChange);
        }
    }
}
