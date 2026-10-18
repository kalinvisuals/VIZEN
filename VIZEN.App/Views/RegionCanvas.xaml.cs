using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VIZEN.App.Models;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Live frame with editable crop regions on top. Drag on empty space to draw a region, drag a region to
/// move it, drag a handle of the selected region to resize it, Delete removes the selected region.
/// All geometry is normalized (0–1) so it is independent of the displayed size.
/// </summary>
public partial class RegionCanvas : UserControl
{
    private const double HandleSize = 10;
    private const double HandleHitRadius = 9;
    private const double DragThreshold = 4;

    [Flags]
    private enum Edge
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
    }

    private enum DragMode
    {
        None,
        Draw,
        Move,
        Resize,
    }

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(ImageSource), typeof(RegionCanvas), new PropertyMetadata(null, OnFrameChanged));

    public static readonly DependencyProperty RegionsProperty = DependencyProperty.Register(
        nameof(Regions), typeof(IEnumerable<CropRegionViewModel>), typeof(RegionCanvas),
        new PropertyMetadata(null, OnRegionsChanged));

    public static readonly DependencyProperty SelectedRegionProperty = DependencyProperty.Register(
        nameof(SelectedRegion), typeof(CropRegionViewModel), typeof(RegionCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((RegionCanvas)d).Redraw()));

    public static readonly DependencyProperty AddRegionCommandProperty = DependencyProperty.Register(
        nameof(AddRegionCommand), typeof(ICommand), typeof(RegionCanvas));

    public static readonly DependencyProperty CommitCommandProperty = DependencyProperty.Register(
        nameof(CommitCommand), typeof(ICommand), typeof(RegionCanvas));

    public static readonly DependencyProperty DeleteCommandProperty = DependencyProperty.Register(
        nameof(DeleteCommand), typeof(ICommand), typeof(RegionCanvas));

    private DragMode _mode;
    private Edge _edges;
    private Point _start;
    private Rect _startBounds;
    private Rect? _drawPreview;
    private int _frameWidth;
    private int _frameHeight;

    public RegionCanvas()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    public ImageSource? Frame
    {
        get => (ImageSource?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public IEnumerable<CropRegionViewModel>? Regions
    {
        get => (IEnumerable<CropRegionViewModel>?)GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    public CropRegionViewModel? SelectedRegion
    {
        get => (CropRegionViewModel?)GetValue(SelectedRegionProperty);
        set => SetValue(SelectedRegionProperty, value);
    }

    /// <summary>Executed with the drawn normalized <see cref="Rect"/> when the user finishes drawing.</summary>
    public ICommand? AddRegionCommand
    {
        get => (ICommand?)GetValue(AddRegionCommandProperty);
        set => SetValue(AddRegionCommandProperty, value);
    }

    /// <summary>Executed when a move/resize ends.</summary>
    public ICommand? CommitCommand
    {
        get => (ICommand?)GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    /// <summary>Executed when Delete is pressed while the canvas has focus.</summary>
    public ICommand? DeleteCommand
    {
        get => (ICommand?)GetValue(DeleteCommandProperty);
        set => SetValue(DeleteCommandProperty, value);
    }

    private IEnumerable<CropRegionViewModel> RegionList => Regions ?? [];

    // ----- Layout -----

    private static void OnFrameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (RegionCanvas)d;
        var frame = (ImageSource?)e.NewValue;
        canvas.FrameImage.Source = frame;
        var (width, height) = frame switch
        {
            BitmapSource b => (b.PixelWidth, b.PixelHeight),
            System.Windows.Interop.D3DImage gpu => (gpu.PixelWidth, gpu.PixelHeight),
            _ => (0, 0),
        };
        var sizeChanged = frame is not null && (width != canvas._frameWidth || height != canvas._frameHeight);
        canvas._frameWidth = width;
        canvas._frameHeight = height;
        if (sizeChanged || frame is null)
        {
            canvas.UpdateSurfaceLayout();
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSurfaceLayout();

    /// <summary>Fits the surface to the frame's aspect ratio inside the available space (like Stretch=Uniform).</summary>
    private void UpdateSurfaceLayout()
    {
        if (_frameWidth == 0 || _frameHeight == 0 || Host.ActualWidth <= 0 || Host.ActualHeight <= 0)
        {
            Surface.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = Math.Min(Host.ActualWidth / _frameWidth, Host.ActualHeight / _frameHeight);
        var width = _frameWidth * scale;
        var height = _frameHeight * scale;
        Surface.Width = width;
        Surface.Height = height;
        Surface.Margin = new Thickness((Host.ActualWidth - width) / 2, (Host.ActualHeight - height) / 2, 0, 0);
        Surface.Visibility = Visibility.Visible;
        Redraw();
    }

    // ----- Region collection tracking -----

    private static void OnRegionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (RegionCanvas)d;
        if (e.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= canvas.OnCollectionChanged;
        }

        if (e.OldValue is IEnumerable<CropRegionViewModel> oldRegions)
        {
            foreach (var region in oldRegions)
            {
                region.PropertyChanged -= canvas.OnRegionPropertyChanged;
            }
        }

        if (e.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += canvas.OnCollectionChanged;
        }

        foreach (var region in canvas.RegionList)
        {
            region.PropertyChanged += canvas.OnRegionPropertyChanged;
        }

        canvas.Redraw();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var region in e.OldItems?.OfType<CropRegionViewModel>() ?? [])
        {
            region.PropertyChanged -= OnRegionPropertyChanged;
        }

        foreach (var region in e.NewItems?.OfType<CropRegionViewModel>() ?? [])
        {
            region.PropertyChanged += OnRegionPropertyChanged;
        }

        Redraw();
    }

    private void OnRegionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(CropRegionViewModel.Bounds) or nameof(CropRegionViewModel.Name))
        {
            Redraw();
        }
    }

    // ----- Rendering -----

    private Rect ToSurface(Rect normalized) => new(
        normalized.X * Surface.Width, normalized.Y * Surface.Height,
        normalized.Width * Surface.Width, normalized.Height * Surface.Height);

    private Point ToNormalized(Point p) => new(p.X / Surface.Width, p.Y / Surface.Height);

    private void Redraw()
    {
        if (Surface.Visibility != Visibility.Visible || double.IsNaN(Surface.Width))
        {
            return;
        }

        Overlay.Children.Clear();
        var accent = (Brush)FindResource("Brush.Accent");
        var full = new Rect(0, 0, Surface.Width, Surface.Height);

        // Darken everything outside the regions.
        Geometry clear = Geometry.Empty;
        foreach (var region in RegionList)
        {
            clear = Geometry.Combine(clear, new RectangleGeometry(ToSurface(region.Bounds)), GeometryCombineMode.Union, null);
        }

        if (_drawPreview is { } preview)
        {
            clear = Geometry.Combine(clear, new RectangleGeometry(ToSurface(preview)), GeometryCombineMode.Union, null);
        }

        Shade.Data = RegionList.Any() || _drawPreview is not null
            ? Geometry.Combine(new RectangleGeometry(full), clear, GeometryCombineMode.Exclude, null)
            : null;

        foreach (var region in RegionList)
        {
            var selected = ReferenceEquals(region, SelectedRegion);
            var rect = ToSurface(region.Bounds);
            AddBox(rect, selected ? accent : new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), selected ? 2 : 1.5, null);
            AddLabel(region.Name, rect, selected ? accent : new SolidColorBrush(Color.FromArgb(200, 20, 24, 34)));
            if (selected)
            {
                foreach (var handle in HandlePoints(rect))
                {
                    var square = new Rectangle
                    {
                        Width = HandleSize,
                        Height = HandleSize,
                        Fill = Brushes.White,
                        Stroke = accent,
                        StrokeThickness = 1.5,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(square, handle.Point.X - HandleSize / 2);
                    Canvas.SetTop(square, handle.Point.Y - HandleSize / 2);
                    Overlay.Children.Add(square);
                }
            }
        }

        if (_drawPreview is { } drawing)
        {
            AddBox(ToSurface(drawing), accent, 1.5, new DoubleCollection { 4, 3 });
        }
    }

    private void AddBox(Rect rect, Brush stroke, double thickness, DoubleCollection? dash)
    {
        var box = new Rectangle
        {
            Width = Math.Max(rect.Width, 1),
            Height = Math.Max(rect.Height, 1),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeDashArray = dash,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(box, rect.X);
        Canvas.SetTop(box, rect.Y);
        Overlay.Children.Add(box);
    }

    private void AddLabel(string text, Rect rect, Brush background)
    {
        var label = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(0, 0, 6, 0),
            Padding = new Thickness(6, 2, 7, 3),
            IsHitTestVisible = false,
            MaxWidth = Math.Max(rect.Width, 40),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        Canvas.SetLeft(label, rect.X);
        Canvas.SetTop(label, rect.Y);
        Overlay.Children.Add(label);
    }

    private static IEnumerable<(Point Point, Edge Edges)> HandlePoints(Rect r)
    {
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        yield return (new Point(r.Left, r.Top), Edge.Left | Edge.Top);
        yield return (new Point(cx, r.Top), Edge.Top);
        yield return (new Point(r.Right, r.Top), Edge.Right | Edge.Top);
        yield return (new Point(r.Right, cy), Edge.Right);
        yield return (new Point(r.Right, r.Bottom), Edge.Right | Edge.Bottom);
        yield return (new Point(cx, r.Bottom), Edge.Bottom);
        yield return (new Point(r.Left, r.Bottom), Edge.Left | Edge.Bottom);
        yield return (new Point(r.Left, cy), Edge.Left);
    }

    // ----- Hit testing -----

    private Edge HitHandle(Point p)
    {
        if (SelectedRegion is null)
        {
            return Edge.None;
        }

        foreach (var (point, edges) in HandlePoints(ToSurface(SelectedRegion.Bounds)))
        {
            if (Math.Abs(p.X - point.X) <= HandleHitRadius && Math.Abs(p.Y - point.Y) <= HandleHitRadius)
            {
                return edges;
            }
        }

        return Edge.None;
    }

    private CropRegionViewModel? HitRegion(Point p)
    {
        // Prefer the selected region, then the topmost (last drawn).
        if (SelectedRegion is not null && ToSurface(SelectedRegion.Bounds).Contains(p))
        {
            return SelectedRegion;
        }

        return RegionList.LastOrDefault(r => ToSurface(r.Bounds).Contains(p));
    }

    private static Cursor CursorFor(Edge edges) => edges switch
    {
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        Edge.Left or Edge.Right => Cursors.SizeWE,
        Edge.Top or Edge.Bottom => Cursors.SizeNS,
        _ => Cursors.Cross,
    };

    // ----- Mouse interaction -----

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (Surface.Visibility != Visibility.Visible)
        {
            return;
        }

        var p = e.GetPosition(Overlay);
        _start = p;

        var edges = HitHandle(p);
        if (edges != Edge.None && SelectedRegion is not null)
        {
            _mode = DragMode.Resize;
            _edges = edges;
            _startBounds = SelectedRegion.Bounds;
        }
        else if (HitRegion(p) is { } region)
        {
            SelectedRegion = region;
            _mode = DragMode.Move;
            _startBounds = region.Bounds;
            Overlay.Cursor = Cursors.SizeAll;
        }
        else
        {
            _mode = DragMode.Draw;
            _drawPreview = null;
        }

        Host.CaptureMouse();
        e.Handled = true;
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        switch (_mode)
        {
            case DragMode.None:
                var edges = HitHandle(p);
                Overlay.Cursor = edges != Edge.None ? CursorFor(edges) : HitRegion(p) is not null ? Cursors.SizeAll : Cursors.Cross;
                break;

            case DragMode.Draw:
                if ((p - _start).Length >= DragThreshold)
                {
                    _drawPreview = RegionMath.FromPoints(ToNormalized(_start), ToNormalized(p));
                    Redraw();
                }

                break;

            case DragMode.Move when SelectedRegion is not null:
                var delta = ToNormalized(p) - ToNormalized(_start);
                SelectedRegion.Bounds = RegionMath.ClampInside(new Rect(
                    _startBounds.X + delta.X, _startBounds.Y + delta.Y, _startBounds.Width, _startBounds.Height));
                break;

            case DragMode.Resize when SelectedRegion is not null:
                SelectedRegion.Bounds = Resize(_startBounds, _edges, ToNormalized(p), SelectedRegion.Aspect);
                break;
        }
    }

    private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        var mode = _mode;
        var drawn = _drawPreview;
        EndDrag();

        if (mode == DragMode.Draw && drawn is { } rect && IsBigEnough(rect))
        {
            AddRegionCommand?.Execute(rect);
        }
        else if (mode is DragMode.Move or DragMode.Resize)
        {
            CommitCommand?.Execute(null);
        }
    }

    private void OnOverlayLostCapture(object sender, MouseEventArgs e)
    {
        if (_mode != DragMode.None)
        {
            var wasEditing = _mode is DragMode.Move or DragMode.Resize;
            EndDrag();
            if (wasEditing)
            {
                CommitCommand?.Execute(null);
            }
        }
    }

    private void EndDrag()
    {
        _mode = DragMode.None;
        _edges = Edge.None;
        _drawPreview = null;
        if (Host.IsMouseCaptured)
        {
            Host.ReleaseMouseCapture();
        }

        Redraw();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DeleteCommand?.CanExecute(null) == true)
        {
            DeleteCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ----- Geometry -----

    private double MinWidth01 => _frameWidth > 0 ? RegionMath.MinEdgePixels / _frameWidth : 0.02;

    private double MinHeight01 => _frameHeight > 0 ? RegionMath.MinEdgePixels / _frameHeight : 0.02;

    private bool IsBigEnough(Rect r) => r.Width >= MinWidth01 && r.Height >= MinHeight01;

    /// <summary>Moves the dragged edges to <paramref name="p"/>, honouring minimum size and an optional locked aspect.</summary>
    private Rect Resize(Rect start, Edge edges, Point p, double? pixelAspect)
    {
        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        if (edges.HasFlag(Edge.Left))
        {
            left = Math.Clamp(p.X, 0, right - MinWidth01);
        }

        if (edges.HasFlag(Edge.Right))
        {
            right = Math.Clamp(p.X, left + MinWidth01, 1);
        }

        if (edges.HasFlag(Edge.Top))
        {
            top = Math.Clamp(p.Y, 0, bottom - MinHeight01);
        }

        if (edges.HasFlag(Edge.Bottom))
        {
            bottom = Math.Clamp(p.Y, top + MinHeight01, 1);
        }

        var rect = new Rect(new Point(left, top), new Point(right, bottom));
        if (pixelAspect is not { } aspect)
        {
            return rect;
        }

        // Locked aspect: derive the free dimension from the dragged one, anchored at the opposite edge/corner.
        var norm = RegionMath.NormalizedAspect(aspect, _frameWidth, _frameHeight);
        var horizontalDrag = edges.HasFlag(Edge.Left) || edges.HasFlag(Edge.Right);
        var width = horizontalDrag ? rect.Width : rect.Height * norm;
        var height = width / norm;

        // Shrink to stay inside the source.
        var maxWidth = edges.HasFlag(Edge.Left) ? start.Right : 1 - start.Left;
        var maxHeight = edges.HasFlag(Edge.Top) ? start.Bottom : 1 - start.Top;
        var fit = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        width *= fit;
        height *= fit;

        var x = edges.HasFlag(Edge.Left) ? start.Right - width : start.Left;
        var y = edges.HasFlag(Edge.Top) ? start.Bottom - height : start.Top;
        if (edges is Edge.Left or Edge.Right)
        {
            y = Math.Clamp(start.Top + (start.Height - height) / 2, 0, 1 - height);
        }
        else if (edges is Edge.Top or Edge.Bottom)
        {
            x = Math.Clamp(start.Left + (start.Width - width) / 2, 0, 1 - width);
        }

        return RegionMath.ClampInside(new Rect(x, y, width, height));
    }
}
