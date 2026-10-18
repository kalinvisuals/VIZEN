using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VIZEN.App.Models;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Smart Draw's drawing surface on one monitor (its work area, so the taskbar stays reachable). A transparent,
/// topmost, per-pixel-alpha window above VIZEN's overlays (topmost band 1), rendered by WPF in software (no GPU
/// readback) and only when something changes: every annotation is its own cached visual, and only the annotation
/// being drawn is redrawn while the mouse moves. Draw mode: an almost invisible background (alpha 1) takes the mouse.
/// Pass-through: fully transparent plus WS_EX_TRANSPARENT, so every click reaches the window underneath.
/// </summary>
internal sealed class DrawCanvasWindow : Window
{
    private static readonly Brush DrawBackground = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    private readonly SmartDrawViewModel _vm;
    private readonly TopmostLayerStyle _style;
    private readonly Grid _root = new();
    private readonly AnnotationHost _host = new();
    private readonly Canvas _editorLayer = new();
    private readonly Border _indicator;
    private readonly TranslateTransform _offset = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private Annotation? _live;
    private Point _dragStart;
    private readonly Grid _eraserRing = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private static readonly IFrameClock FrameClock = new WpfFrameClock();
    private EraseSession? _erase;
    private TextBox? _editor;
    private Annotation? _editing;
    private Point _editorOrigin;
    private double _dpi = 1;

    public DrawCanvasWindow(SmartDrawViewModel vm, MonitorInfo monitor)
    {
        _vm = vm;
        Monitor = monitor;
        Title = "VIZEN Smart Draw";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FocusVisualStyle = null;

        // Annotations are in monitor pixels: shift to the work area, then scale pixels to this window's DIPs.
        _offset.X = -(monitor.WorkLeft - monitor.Left);
        _offset.Y = -(monitor.WorkTop - monitor.Top);
        _host.RenderTransform = new TransformGroup { Children = { _offset, _scale } };
        _host.IsHitTestVisible = false;
        _host.Opacity = vm.Opacity;

        _indicator = new Border
        {
            BorderThickness = new Thickness(3),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Accent"),
            IsHitTestVisible = false,
            Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10),
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.FindResource("Brush.AccentGradient"),
                Child = new TextBlock
                {
                    Text = Localization.Loc.T("Draw.Indicator"),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                },
            },
        };

        _root.Children.Add(_host);
        _root.Children.Add(_editorLayer);
        _root.Children.Add(_indicator);
        Content = _root;

        _style = new TopmostLayerStyle(this, () => _vm.IsClickThrough, perPixelAlpha: true) { Band = 1 };
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, e) => ApplyDpi(e.NewDpi.DpiScaleX);
        _root.MouseLeftButtonDown += OnMouseDown;
        _root.MouseMove += OnMouseMove;
        _root.MouseLeave += (_, _) => _eraserRing.Visibility = Visibility.Collapsed;

        // The eraser's circle, drawn at exactly the area it erases (DIPs, so it stays aligned at any scaling): a white
        // ring with a dark outline, visible on light and dark backgrounds.
        _eraserRing.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), StrokeThickness = 3 });
        _eraserRing.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Brushes.White, StrokeThickness = 1.4 });
        _editorLayer.Children.Add(_eraserRing);
        UpdateRingSize();
        _root.MouseLeftButtonUp += OnMouseUp;
        _root.LostMouseCapture += (_, _) => FinishGesture(commit: true);
        KeyDown += OnKeyDown;

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Document.Changed += OnDocumentChanged;
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Document.Changed -= OnDocumentChanged;
            _host.Clear();
        };

        _host.Reset(_vm.Document.Items.Where(a => a.Monitor == Monitor.Key));
        ApplyMode();
    }

    public MonitorInfo Monitor { get; }

    /// <summary>Set by the presenter when the window really closes.</summary>
    internal bool AllowClose { get; set; }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Number of annotation visuals (tests and diagnostics).</summary>
    internal int VisualCount => _host.Count;

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 while drawing: leave draw mode instead of breaking the session.
        if (!AllowClose)
        {
            e.Cancel = true;
            _vm.SetDrawMode(false);
        }

        base.OnClosing(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Handle;

        // Software rendering: a layered window rendered on the GPU would be read back to system memory on every change.
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
        {
            target.RenderMode = RenderMode.SoftwareOnly;
        }

        _style.Attach(systemFrame: false);

        // Exactly the monitor's work area, in physical pixels (DIP placement is ambiguous across monitors of different DPI).
        SetWindowPos(hwnd, IntPtr.Zero, Monitor.WorkLeft, Monitor.WorkTop, Monitor.WorkWidth, Monitor.WorkHeight, 0x0014 /* NOZORDER | NOACTIVATE */);
        ApplyDpi(VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    private void ApplyDpi(double scale)
    {
        _dpi = scale > 0 ? scale : 1;
        _scale.ScaleX = _scale.ScaleY = 1 / _dpi;
    }

    // ----- View model -----

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SmartDrawViewModel.IsClickThrough):
            case nameof(SmartDrawViewModel.IsDrawMode):
            case nameof(SmartDrawViewModel.AnnotationsVisible):
            case nameof(SmartDrawViewModel.Tool):
                ApplyMode();
                break;
            case nameof(SmartDrawViewModel.Opacity):
                _host.Opacity = _vm.Opacity;
                break;
            case nameof(SmartDrawViewModel.EraserSize):
                UpdateRingSize();
                break;
        }
    }

    private bool IsErasing => _vm.IsDrawMode && _vm.Tool == DrawTool.Eraser;

    private void UpdateRingSize()
    {
        _eraserRing.Width = _eraserRing.Height = _vm.EraserSize;
    }

    /// <summary>Eraser radius in monitor pixels (the diameter setting is in DIPs).</summary>
    private double EraserRadius => DrawCoordinates.EraserRadius(_vm.EraserSize, _dpi);

    private void ApplyMode()
    {
        if (!_vm.IsDrawMode || (_erase is not null && _vm.Tool != DrawTool.Eraser))
        {
            // Leaving draw mode, or another tool picked in the middle of an eraser drag: finish it cleanly.
            FinishGesture(commit: true);
            CommitText();
        }
        else if (!_vm.AnnotationsVisible || _vm.Tool != DrawTool.Text)
        {
            CommitText(); // drawings hidden or another tool picked: an open text editor is finished
        }

        _root.Background = _vm.IsDrawMode && _vm.AnnotationsVisible ? DrawBackground : null;
        _indicator.Visibility = _vm.IsDrawMode ? Visibility.Visible : Visibility.Collapsed;
        _root.Cursor = !_vm.IsDrawMode ? null : _vm.Tool switch
        {
            DrawTool.Text => Cursors.IBeam,
            DrawTool.Eraser => Cursors.None, // the eraser's own circle is the cursor
            _ => Cursors.Cross,
        };
        if (!IsErasing)
        {
            _eraserRing.Visibility = Visibility.Collapsed;
        }
        _style.ApplyExtendedStyle();
        if (_vm.AnnotationsVisible)
        {
            if (!IsVisible)
            {
                Show();
                _style.ApplyExtendedStyle();
            }
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    private void OnDocumentChanged(object? sender, DrawingChange change)
    {
        if (change.Kind == DrawingChangeKind.Reset)
        {
            _host.Reset(change.Annotations.Where(a => a.Monitor == Monitor.Key));
            return;
        }

        if (change.Kind == DrawingChangeKind.Replaced)
        {
            // Only the annotations that went out and came in are redrawn, each at its stacking position.
            foreach (var a in change.Removed ?? [])
            {
                _host.Remove(a);
            }

            var incoming = change.Annotations.Where(a => a.Monitor == Monitor.Key).ToHashSet();
            var position = 0;
            foreach (var a in _vm.Document.Items)
            {
                if (a.Monitor != Monitor.Key)
                {
                    continue;
                }

                if (incoming.Contains(a))
                {
                    _host.InsertAt(a, position);
                    position++;
                }
                else if (_host.Contains(a))
                {
                    position++;
                }
            }

            return;
        }

        foreach (var a in change.Annotations.Where(a => a.Monitor == Monitor.Key))
        {
            switch (change.Kind)
            {
                case DrawingChangeKind.Added:
                    _host.Add(a);
                    break;
                case DrawingChangeKind.Removed:
                    _host.Remove(a);
                    break;
                case DrawingChangeKind.Updated:
                    _host.Update(a);
                    break;
            }
        }
    }

    // ----- Drawing -----

    /// <summary>Mouse position in monitor pixels.</summary>
    private Point ToMonitor(MouseEventArgs e) => DrawCoordinates.ToMonitor(e.GetPosition(_root), _dpi, new Vector(-_offset.X, -_offset.Y));

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsDrawMode)
        {
            return;
        }

        var p = ToMonitor(e);
        if (_vm.Tool == DrawTool.Text)
        {
            CommitText();
            BeginText(p);
            e.Handled = true;
            return;
        }

        CommitText();
        _dragStart = p;
        var thickness = _vm.Thickness * _dpi;
        switch (_vm.Tool)
        {
            case DrawTool.Pen:
                _live = new Annotation { Kind = AnnotationKind.Pen, Monitor = Monitor.Key, Color = _vm.Color, Thickness = thickness };
                DrawingGeometry.AddPenPoint(_live.Points, p);
                break;
            case DrawTool.Arrow:
            case DrawTool.Rectangle:
            case DrawTool.Ellipse:
                _live = new Annotation
                {
                    Kind = _vm.Tool switch { DrawTool.Arrow => AnnotationKind.Arrow, DrawTool.Rectangle => AnnotationKind.Rectangle, _ => AnnotationKind.Ellipse },
                    Monitor = Monitor.Key, Color = _vm.Color, Thickness = thickness, Head = _vm.ArrowHead,
                    X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y,
                };
                break;
            case DrawTool.Eraser:
                _erase = new EraseSession(new EraseGesture(_vm.Document.Items.Where(a => a.Monitor == Monitor.Key), _vm.EraserMode), p, EraserRadius, FrameClock);
                _erase.FrameReady += OnEraseFrame;
                _erase.Start();
                break;
        }

        _host.SetLive(_live);
        _root.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (IsErasing)
        {
            var dip = e.GetPosition(_root);
            Canvas.SetLeft(_eraserRing, dip.X - _vm.EraserSize / 2);
            Canvas.SetTop(_eraserRing, dip.Y - _vm.EraserSize / 2);
            _eraserRing.Visibility = Visibility.Visible;
        }

        if (!_root.IsMouseCaptured)
        {
            return;
        }

        var p = ToMonitor(e);
        if (_erase is not null)
        {
            _erase.MoveTo(p); // erased on the next rendering pass, together with the other positions received by then
            return;
        }

        if (_live is null)
        {
            return;
        }

        switch (_live.Kind)
        {
            case AnnotationKind.Pen:
                if (!DrawingGeometry.AddPenPoint(_live.Points, p))
                {
                    return; // too close to the last point: nothing new to draw
                }

                break;
            case AnnotationKind.Ellipse:
            case AnnotationKind.Rectangle:
            {
                // Shift: circle / square, growing from the start point in the drag direction.
                var r = DrawingGeometry.EllipseBounds(_dragStart, p, circle: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                (_live.X1, _live.Y1, _live.X2, _live.Y2) = (r.Left, r.Top, r.Right, r.Bottom);
                break;
            }
            default:
                (_live.X2, _live.Y2) = (p.X, p.Y);
                break;
        }

        _host.SetLive(_live);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_root.IsMouseCaptured)
        {
            _root.ReleaseMouseCapture(); // → FinishGesture
        }
    }

    private void FinishGesture(bool commit)
    {
        var live = _live;
        _live = null;
        _host.SetLive((Annotation?)null);
        if (_erase is not null)
        {
            var session = _erase;
            _erase = null;

            // Erases what is still queued, stops the frame callback, then applies the drag as one undo step: the
            // document raises Replaced for just the strokes involved, then the preview visuals are removed (both before
            // the next frame is drawn, so nothing flickers).
            session.Finish(_vm.Document, commit);
            session.FrameReady -= OnEraseFrame;
            _host.ClearErasePreview();
        }

        if (commit && live is not null)
        {
            _vm.AddAnnotation(live);
        }
    }

    /// <summary>
    /// Localized preview update for one frame of erasing: only the pieces that changed in this frame are touched —
    /// a stroke cut for the first time is hidden, a piece cut again loses its own visual, each new piece gets its
    /// own visual, objects to be removed whole are dimmed. Pieces erased earlier in the drag are not redrawn, so WPF
    /// invalidates only the old and new geometry of what changed. The document changes only when the drag ends.
    /// </summary>
    private void OnEraseFrame(EraseFrame frame)
    {
        foreach (var a in frame.Removed)
        {
            if (!_host.HidePiece(a))
            {
                _host.RemovePreview(a);
            }
        }

        foreach (var a in frame.Added)
        {
            _host.AddPreview(a);
        }

        foreach (var a in frame.WholeRemoved)
        {
            _host.PreviewErase(a);
        }
    }

    // ----- Text -----

    private void BeginText(Point p)
    {
        var existing = _vm.Document.Items.LastOrDefault(a => a.Monitor == Monitor.Key && a.Kind == AnnotationKind.Text && DrawingGeometry.HitTest(a, p, 4 * _dpi));
        _editing = existing;
        var fontPx = existing?.FontSize ?? _vm.FontSize * _dpi;
        _editorOrigin = existing is null ? p : new Point(existing.X1, existing.Y1);
        if (existing is not null)
        {
            _host.Hide(existing);
        }

        var color = (Color)ColorConverter.ConvertFromString(existing?.Color ?? _vm.Color);
        _editor = new TextBox
        {
            Text = existing?.Text ?? string.Empty,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = fontPx / _dpi,
            Foreground = new SolidColorBrush(color),
            CaretBrush = new SolidColorBrush(color),
            Background = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Accent"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            MinWidth = 80,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationPropertiesHelper.Name(_editor, Localization.Loc.T("Draw.TextEditor"));
        Canvas.SetLeft(_editor, (_editorOrigin.X + _offset.X) / _dpi - 1);
        Canvas.SetTop(_editor, (_editorOrigin.Y + _offset.Y) / _dpi - 1);
        var editor = _editor;
        editor.PreviewKeyDown += OnEditorKeyDown;
        editor.LostKeyboardFocus += OnEditorLostFocus;
        editor.CaretIndex = editor.Text.Length;
        _editorLayer.Children.Add(editor);
        Activate(); // typing needs the keyboard focus (draw mode only)
        editor.Focus();
    }

    /// <summary>
    /// Focus moved to something else in VIZEN: the text is done. When the whole window loses the keyboard (a game or
    /// another app took the foreground, NewFocus is null) the editor stays open with its text; clicking it again
    /// continues typing, and a click elsewhere, Esc / Enter or leaving draw mode finishes it.
    /// </summary>
    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender == _editor && e.NewFocus is not null)
        {
            CommitText();
        }
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitText(cancel: true);
            e.Handled = true;
        }
    }

    private void CommitText(bool cancel = false)
    {
        if (_editor is null)
        {
            return;
        }

        var editor = _editor;
        var editing = _editing;
        _editor = null;
        _editing = null;
        _editorLayer.Children.Remove(editor);
        var text = editor.Text.TrimEnd();
        if (editing is not null)
        {
            _host.Show(editing);
            if (!cancel)
            {
                var size = AnnotationRenderer.MeasureText(text, editing.FontSize);
                _vm.Document.UpdateText(editing, text, size.Width, size.Height);
            }

            return;
        }

        if (!cancel && text.Trim().Length > 0)
        {
            var fontPx = editor.FontSize * _dpi;
            var size = AnnotationRenderer.MeasureText(text, fontPx);
            _vm.AddAnnotation(new Annotation
            {
                Kind = AnnotationKind.Text, Monitor = Monitor.Key, Color = _vm.Color, Text = text, FontSize = fontPx,
                X1 = _editorOrigin.X, Y1 = _editorOrigin.Y, TextWidth = size.Width, TextHeight = size.Height,
            });
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor is not null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _vm.SetDrawMode(false); // the way out of draw mode from the keyboard
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            _vm.Document.Undo();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            _vm.Document.Redo();
            e.Handled = true;
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>One cached visual per annotation, plus the one being drawn.</summary>
    private sealed class AnnotationHost : FrameworkElement
    {
        private readonly VisualCollection _children;
        private readonly Dictionary<Annotation, DrawingVisual> _visuals = [];
        private readonly DrawingVisual _live = new();
        private readonly Dictionary<Annotation, DrawingVisual> _previews = [];
        private readonly HashSet<DrawingVisual> _dimmed = [];

        public AnnotationHost()
        {
            _children = new VisualCollection(this) { _live };
        }

        public int Count => _visuals.Count;

        protected override int VisualChildrenCount => _children.Count;

        protected override Visual GetVisualChild(int index) => _children[index];

        public void Reset(IEnumerable<Annotation> annotations)
        {
            Clear();
            foreach (var a in annotations)
            {
                Add(a);
            }
        }

        public void Clear()
        {
            _children.Clear();
            _visuals.Clear();
            _previews.Clear();
            _dimmed.Clear();
            _children.Add(_live);
        }

        public void Add(Annotation a)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                AnnotationRenderer.Render(dc, a);
            }

            _visuals[a] = visual;
            _children.Insert(_children.Count - 1, visual); // the live visual stays on top
        }

        public void Remove(Annotation a)
        {
            if (_visuals.Remove(a, out var visual))
            {
                _children.Remove(visual);
                _dimmed.Remove(visual);
            }
        }

        public void Update(Annotation a)
        {
            if (_visuals.TryGetValue(a, out var visual))
            {
                using var dc = visual.RenderOpen();
                AnnotationRenderer.Render(dc, a);
            }
        }

        public void Hide(Annotation a)
        {
            if (_visuals.TryGetValue(a, out var visual))
            {
                visual.Opacity = 0;
            }
        }

        public void Show(Annotation a)
        {
            if (_visuals.TryGetValue(a, out var visual))
            {
                visual.Opacity = 1;
            }
        }

        // ----- Eraser preview (only what changed in a frame is touched) -----

        /// <summary>Dims an annotation the eraser will remove whole.</summary>
        public void PreviewErase(Annotation a)
        {
            if (_visuals.TryGetValue(a, out var visual))
            {
                visual.Opacity = 0.25;
                _dimmed.Add(visual);
            }
        }

        /// <summary>Hides a document stroke the eraser just cut (its pieces are shown as previews). False if it is not one.</summary>
        public bool HidePiece(Annotation a)
        {
            if (!_visuals.TryGetValue(a, out var visual))
            {
                return false;
            }

            visual.Opacity = 0;
            _dimmed.Add(visual);
            return true;
        }

        /// <summary>A piece left by the eraser, in its own visual (redrawn never again unless it is cut again).</summary>
        public void AddPreview(Annotation a)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                AnnotationRenderer.Render(dc, a);
            }

            _previews[a] = visual;
            _children.Insert(_children.Count - 1, visual);
        }

        public void RemovePreview(Annotation a)
        {
            if (_previews.Remove(a, out var visual))
            {
                _children.Remove(visual);
            }
        }

        /// <summary>Ends the preview: removes the piece visuals and restores what was hidden or dimmed.</summary>
        public void ClearErasePreview()
        {
            foreach (var visual in _previews.Values)
            {
                _children.Remove(visual);
            }

            _previews.Clear();
            foreach (var visual in _dimmed)
            {
                visual.Opacity = 1;
            }

            _dimmed.Clear();
        }

        public bool Contains(Annotation a) => _visuals.ContainsKey(a);

        /// <summary>Inserts an annotation's visual at a stacking position among the annotation visuals.</summary>
        public void InsertAt(Annotation a, int index)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                AnnotationRenderer.Render(dc, a);
            }

            _visuals[a] = visual;
            _children.Insert(Math.Clamp(index, 0, _children.Count - 1), visual);
        }

        public void SetLive(Annotation? a)
        {
            using var dc = _live.RenderOpen();
            if (a is not null)
            {
                AnnotationRenderer.Render(dc, a);
            }
        }
    }
}

/// <summary>
/// WPF's rendering callback (CompositionTarget.Rendering): raised once per WPF rendering pass, which usually follows the
/// display refresh but is not guaranteed to. Subscribed only while the eraser has queued work.
/// </summary>
internal sealed class WpfFrameClock : IFrameClock
{
    public void Subscribe(EventHandler handler) => CompositionTarget.Rendering += handler;

    public void Unsubscribe(EventHandler handler) => CompositionTarget.Rendering -= handler;
}

/// <summary>Small helper so code-only windows can name controls for UI Automation.</summary>
internal static class AutomationPropertiesHelper
{
    public static void Name(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
}
