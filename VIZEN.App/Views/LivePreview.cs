using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using VIZEN.App.Capture;

namespace VIZEN.App.Views;

/// <summary>
/// In-app live preview of a capture, drawn by WPF from the capture's CPU frames (capped by the consumer's lease).
/// No native video window here: a native child window with a swap chain inside the main window left frozen pictures
/// of a previous page on screen over the next one (titles over titles, the Sources panel over Gaming), which no
/// repaint or resize cleared. Overlays, in their own windows, keep the native presenter.
/// </summary>
public sealed class LivePreview : Grid
{
    public static readonly DependencyProperty CaptureProperty = DependencyProperty.Register(
        nameof(Capture), typeof(LiveCapture), typeof(LivePreview), new PropertyMetadata(null, (d, _) => ((LivePreview)d).OnCaptureChanged()));

    public static readonly DependencyProperty ViewboxProperty = DependencyProperty.Register(
        nameof(Viewbox), typeof(Rect), typeof(LivePreview), new PropertyMetadata(new Rect(0, 0, 1, 1), (d, _) => ((LivePreview)d).OnViewboxChanged()));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(LivePreview), new PropertyMetadata(0.0, (d, _) => ((LivePreview)d).OnCornerRadiusChanged()));

    private readonly Rectangle _video = new();
    private readonly ImageBrush _brush = new() { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Uniform };

    public LivePreview()
    {
        RenderOptions.SetBitmapScalingMode(_video, BitmapScalingMode.Linear);
        _video.Fill = _brush;
        Children.Add(_video);
    }

    public LiveCapture? Capture
    {
        get => (LiveCapture?)GetValue(CaptureProperty);
        set => SetValue(CaptureProperty, value);
    }

    /// <summary>Part of the frame to show (0..1); the whole frame by default.</summary>
    public Rect Viewbox
    {
        get => (Rect)GetValue(ViewboxProperty);
        set => SetValue(ViewboxProperty, value);
    }

    /// <summary>Rounded corners of the video (device-independent pixels).</summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private void OnCaptureChanged() =>
        BindingOperations.SetBinding(_brush, ImageBrush.ImageSourceProperty, new Binding(nameof(LiveCapture.Frame)) { Source = Capture });

    private void OnViewboxChanged() => _brush.Viewbox = Viewbox;

    private void OnCornerRadiusChanged() => _video.RadiusX = _video.RadiusY = CornerRadius;
}
