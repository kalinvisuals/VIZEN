using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// The floating window of one Smart Note: borderless, optionally always on top (kept above borderless games by
/// <see cref="TopmostKeeper"/>, like the overlays), adjustable opacity, optional click-through. Shown without taking
/// the focus; clicking into the text activates it for typing. Plain WPF content that is redrawn only when it changes.
/// </summary>
public partial class NoteWindow : Window
{
    private readonly NoteViewModel _note;
    private readonly TopmostLayerStyle _style;
    private bool _placing;

    public NoteWindow(NoteViewModel note)
    {
        _note = note;
        InitializeComponent();
        DataContext = note;
        _style = new TopmostLayerStyle(this, () => _note.ClickThrough, perPixelAlpha: true);
        _style.SetKeepOnTop(note.AlwaysOnTop);
        ApplyLock();
        SourceInitialized += (_, _) =>
        {
            _style.Attach(systemFrame: false);
            _style.ApplyOpacity(_note.Opacity);
        };
        LocationChanged += (_, _) => TrackBounds();
        SizeChanged += (_, _) => TrackBounds();
        note.PropertyChanged += OnNotePropertyChanged;
        Closed += (_, _) => note.PropertyChanged -= OnNotePropertyChanged;
    }

    /// <summary>Set by the presenter when the window really closes (notes deleted, VIZEN exiting).</summary>
    internal bool AllowClose { get; set; }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Places the window at <paramref name="bounds"/> (device-independent units).</summary>
    public void Place(Rect bounds)
    {
        _placing = true;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = Math.Max(MinWidth, bounds.Width);
        Height = Math.Max(MinHeight, bounds.Height);
        _placing = false;
    }

    /// <summary>Shows the note without activating it (a game or another app keeps the focus).</summary>
    public void ShowNote()
    {
        if (!IsVisible)
        {
            Show();
            _style.ApplyExtendedStyle();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 on a focused note hides it instead: the window belongs to the presenter. When VIZEN exits, WPF closes
        // every window: let it close and keep IsShown, so the note comes back at the next start.
        if (!AllowClose && !NotePresenter.IsExiting)
        {
            e.Cancel = true;
            _note.IsShown = false;
        }

        base.OnClosing(e);
    }

    private void OnNotePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NoteViewModel.Opacity):
                _style.ApplyOpacity(_note.Opacity);
                break;
            case nameof(NoteViewModel.ClickThrough):
                _style.ApplyExtendedStyle();
                break;
            case nameof(NoteViewModel.AlwaysOnTop):
                _style.SetKeepOnTop(_note.AlwaysOnTop);
                break;
            case nameof(NoteViewModel.Locked):
                ApplyLock();
                break;
        }
    }

    /// <summary>Locked: no resizing from the edges and no dragging from the header.</summary>
    private void ApplyLock()
    {
        ResizeMode = _note.Locked ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(_note.Locked ? 0 : 6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
    }

    private void TrackBounds()
    {
        if (!_placing && IsVisible && WindowState == WindowState.Normal && ActualWidth > 0)
        {
            _note.ReportBounds(new Rect(Left, Top, ActualWidth, ActualHeight));
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_note.Locked && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
