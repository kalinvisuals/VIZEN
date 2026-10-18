using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Smart Draw's floating toolbar: compact, movable by its grip, above the drawing surface (topmost band 2) and never
/// activated by a click (the game or app keeps the keyboard focus). Stays on screen while drawings are hidden or in
/// pass-through, so Smart Draw can always be switched back or closed.
/// </summary>
public partial class DrawToolbarWindow : Window
{
    private readonly TopmostLayerStyle _style;
    private readonly Point? _position;

    /// <param name="position">Top-left in physical pixels where the user left the toolbar last time; null = top centre.</param>
    public DrawToolbarWindow(SmartDrawViewModel vm, Point? position)
    {
        InitializeComponent();
        DataContext = vm;
        _position = position;
        _style = new TopmostLayerStyle(this, () => false, perPixelAlpha: true) { Band = 2, NoActivate = true };
        SourceInitialized += (_, _) =>
        {
            if (HwndSource.FromHwnd(Handle) is { CompositionTarget: { } target })
            {
                target.RenderMode = RenderMode.SoftwareOnly; // small layered window: no GPU readback
            }

            _style.Attach(systemFrame: false);
        };
        ContentRendered += (_, _) => Place();
    }

    /// <summary>Set by the presenter when the window really closes.</summary>
    internal bool AllowClose { get; set; }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Top-left in physical pixels (remembered for the next time Smart Draw opens).</summary>
    public Point PixelPosition
    {
        get
        {
            GetWindowRect(Handle, out var r);
            return new Point(r.Left, r.Top);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = !AllowClose; // the close button turns Smart Draw off instead
        base.OnClosing(e);
    }

    private void Place()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = SystemParameters.WorkArea; // primary monitor, DIPs
        var x = _position?.X ?? (work.Left + (work.Width - ActualWidth) / 2) * dpi.DpiScaleX;
        var y = _position?.Y ?? (work.Top + 8) * dpi.DpiScaleY;
        SetWindowPos(Handle, IntPtr.Zero, (int)x, (int)y, 0, 0, 0x0015 /* NOSIZE | NOZORDER | NOACTIVATE */);
    }

    private void OnGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnColorPicked(object sender, RoutedEventArgs e) => ColorToggle.IsChecked = false;

    private void OnColorPopupClosed(object? sender, EventArgs e) => ColorToggle.IsChecked = false;

    private void OnEraserPopupClosed(object? sender, EventArgs e) => EraserSizeToggle.IsChecked = false;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
