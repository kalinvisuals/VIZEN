using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// The floating Smart Audio Mixer: compact, movable by its header, kept above borderless games and VIZEN's overlays
/// (topmost band 2, like Smart Draw's toolbar) and never activated by a click, so the game or app keeps the keyboard.
/// Volumes change with a drag or the mouse wheel over a slider. Small layered window rendered in software (no GPU
/// readback); it is redrawn only when a level, a volume or the list changes.
/// </summary>
public partial class MixerWindow : Window
{
    private readonly AudioMixerViewModel _mixer;
    private readonly TopmostLayerStyle _style;
    private bool _placing;

    public MixerWindow(AudioMixerViewModel mixer)
    {
        _mixer = mixer;
        InitializeComponent();
        DataContext = mixer;
        _style = new TopmostLayerStyle(this, () => false, perPixelAlpha: true) { Band = 2, NoActivate = true };
        SourceInitialized += (_, _) =>
        {
            if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is { CompositionTarget: { } target })
            {
                target.RenderMode = RenderMode.SoftwareOnly;
            }

            _style.Attach(systemFrame: false);
            _style.ApplyOpacity(_mixer.FloatingOpacity);
        };
        mixer.PropertyChanged += OnMixerPropertyChanged;
        Closed += (_, _) => mixer.PropertyChanged -= OnMixerPropertyChanged;
        LocationChanged += (_, _) =>
        {
            if (!_placing && IsVisible)
            {
                _mixer.ReportFloatingPosition(new Point(Left, Top));
            }
        };

        // A size the user chose earlier; otherwise the height follows the number of apps.
        if (mixer.FloatingSize is { } size)
        {
            SizeToContent = SizeToContent.Manual;
            Width = Math.Clamp(size.Width, MinWidth, MaxWidth);
            Height = Math.Clamp(size.Height, MinHeight, MaxHeight);
        }

        SizeChanged += (_, _) =>
        {
            KeepOnScreen(); // the window grows and shrinks: keep it on the desktop (right and bottom edges)

            // Dragging an edge or the grip switches WPF to a manual size: remember it.
            if (!_placing && IsVisible && SizeToContent == SizeToContent.Manual)
            {
                _mixer.ReportFloatingSize(new Size(ActualWidth, ActualHeight));
            }
        };
    }

    /// <summary>Back to the automatic size: default width, height that fits the apps.</summary>
    private void ResetSize()
    {
        _placing = true;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        _placing = false;
        _mixer.ReportFloatingSize(null);
    }

    private void KeepOnScreen()
    {
        if (!IsVisible || ActualWidth <= 0)
        {
            return;
        }

        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        var work = SystemParameters.WorkArea;
        if (Left >= work.Left && Left < work.Right)
        {
            // On the primary monitor: stay inside its work area (never under the taskbar).
            right = work.Right;
            bottom = work.Bottom;
        }

        var left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(Left, right - ActualWidth));
        var top = Math.Max(SystemParameters.VirtualScreenTop, Math.Min(Top, bottom - ActualHeight));
        if (left != Left || top != Top)
        {
            _placing = true;
            Left = left;
            Top = top;
            _placing = false;
        }
    }

    /// <summary>Set by the presenter when the window really closes (VIZEN exiting).</summary>
    internal bool AllowClose { get; set; }

    public void Place(Point topLeft)
    {
        _placing = true;
        Left = topLeft.X;
        Top = topLeft.Y;
        _placing = false;
    }

    /// <summary>Shows the mixer without activating it.</summary>
    public void ShowMixer()
    {
        if (!IsVisible)
        {
            Show();
            KeepOnScreen();
            _style.ApplyExtendedStyle();
        }

        TopmostKeeper.RaiseNow();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides the mixer instead; the window belongs to the presenter.
        if (!AllowClose && !NotePresenter.IsExiting)
        {
            e.Cancel = true;
            _mixer.IsFloatingShown = false;
        }

        base.OnClosing(e);
    }

    private void OnMixerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioMixerViewModel.FloatingOpacity))
        {
            _style.ApplyOpacity(_mixer.FloatingOpacity);
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetSize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
