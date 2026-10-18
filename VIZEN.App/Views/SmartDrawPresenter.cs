using System.Windows;
using Microsoft.Win32;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Opens one <see cref="DrawCanvasWindow"/> per monitor plus the <see cref="DrawToolbarWindow"/> while Smart Draw is on,
/// and closes them all when it is turned off. When monitors are added, removed or change resolution or scaling, the
/// drawing surfaces are rebuilt for the new layout (annotations of a disconnected monitor stay in the drawing).
/// </summary>
public sealed class SmartDrawPresenter : ISmartDrawPresenter
{
    private readonly IMonitorProvider _monitors;
    private readonly List<DrawCanvasWindow> _canvases = [];
    private DrawToolbarWindow? _toolbar;
    private SmartDrawViewModel? _vm;
    private Point? _toolbarPosition;

    public SmartDrawPresenter(IMonitorProvider monitors) => _monitors = monitors;

    public bool IsOpen => _vm is not null;

    /// <summary>Drawing surfaces open now (diagnostics).</summary>
    public int CanvasCount => _canvases.Count;

    public void Open(SmartDrawViewModel drawing)
    {
        if (_vm is not null)
        {
            return;
        }

        _vm = drawing;
        OpenCanvases();
        _toolbar = new DrawToolbarWindow(drawing, _toolbarPosition);
        _toolbar.Show();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        TopmostKeeper.RaiseNow(); // surfaces above the overlays, toolbar above the surfaces
    }

    public void Close()
    {
        if (_vm is null)
        {
            return;
        }

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        CloseCanvases();
        if (_toolbar is not null)
        {
            _toolbarPosition = _toolbar.PixelPosition;
            _toolbar.AllowClose = true;
            _toolbar.Close();
            _toolbar = null;
        }

        _vm = null;
    }

    private void OpenCanvases()
    {
        foreach (var monitor in _monitors.Current())
        {
            var canvas = new DrawCanvasWindow(_vm!, monitor);
            _canvases.Add(canvas);
        }
    }

    private void CloseCanvases()
    {
        foreach (var canvas in _canvases)
        {
            canvas.AllowClose = true;
            canvas.Close();
        }

        _canvases.Clear();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Raised on a system thread: rebuild on the UI thread.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_vm is null)
            {
                return;
            }

            CloseCanvases();
            OpenCanvases();
            TopmostKeeper.RaiseNow();
        });
    }
}
