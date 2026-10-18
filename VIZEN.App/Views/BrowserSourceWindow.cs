using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VIZEN.App.Interop;
using VIZEN.App.Localization;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// A VIZEN-owned window showing one web page through WebView2. Borderless by default (a clean capture source,
/// listed in Sources as "VIZEN Browser"); "Framed" adds a normal title bar so it can be moved.
/// </summary>
public sealed class BrowserSourceWindow : Window
{
    private static Task<CoreWebView2Environment>? _environment;

    private readonly BrowserSourceViewModel _source;
    private readonly WebView2 _web = new();

    public BrowserSourceWindow(BrowserSourceViewModel source)
    {
        _source = source;
        DataContext = source;
        SetBinding(TitleProperty, nameof(BrowserSourceViewModel.WindowTitle));
        Background = Brushes.Black;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResize;
        ApplyFrame();
        Content = _web;

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitializeAsync();
        SizeChanged += (_, _) => TrackSize();
        LocationChanged += (_, _) => _source.ReportPosition(new Point(Left, Top));
        source.PropertyChanged += OnSourcePropertyChanged;
        source.NavigateRequested += OnNavigateRequested;
        source.ReloadRequested += OnReloadRequested;
        source.BackRequested += OnBackRequested;
        source.BringToFrontRequested += OnBringToFrontRequested;
        source.SizeRequested += OnSizeRequested;
        Closed += OnClosed;
    }

    /// <summary>One WebView2 profile for all browser sources, kept in VIZEN's data folder (logins stay there only).</summary>
    private static Task<CoreWebView2Environment> SharedEnvironment() =>
        _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(JsonStore.DataDirectory, "WebView2"));

    private async Task InitializeAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await SharedEnvironment());
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException
            or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _source.ReportError(Loc.F("Browser.InitFailed", ex.Message));
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.IsMuted = _source.Muted;
        _web.ZoomFactor = _source.Zoom;

        core.NavigationStarting += (_, _) => _source.ReportNavigationStarted();
        core.NavigationCompleted += (_, e) => _source.ReportNavigationCompleted(e.IsSuccess, core.Source, core.DocumentTitle,
            e.IsSuccess ? null : e.WebErrorStatus.ToString());
        core.DocumentTitleChanged += (_, _) => _source.ReportTitle(core.DocumentTitle);

        // Pop-ups open in the same window instead of a separate, uncaptured one.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };

        core.Navigate(_source.Source.Url);
    }


    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var corner = 1; // DWMWCP_DONOTROUND: a capture source should be a plain rectangle
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>Restores the saved client size (physical pixels) and position.</summary>
    public void PlaceFromModel()
    {
        var model = _source.Source;
        Left = model.Left != 0 || model.Top != 0 ? model.Left : SystemParameters.WorkArea.Left + 120;
        Top = model.Left != 0 || model.Top != 0 ? model.Top : SystemParameters.WorkArea.Top + 80;
        SizeToContent = SizeToContent.Manual;

        // The size is saved in physical pixels; convert with the scaling of the monitor VIZEN is on, then correct
        // it once more after the first render (when the window knows its own monitor's scaling).
        var dpi = Application.Current?.MainWindow is { } main ? VisualTreeHelper.GetDpi(main) : new DpiScale(1, 1);
        Width = model.PixelWidth / dpi.DpiScaleX;
        Height = model.PixelHeight / dpi.DpiScaleY;
        var pixels = new Size(model.PixelWidth, model.PixelHeight);
        EventHandler? onRendered = null;
        onRendered = (_, _) =>
        {
            ContentRendered -= onRendered;
            OutputWindow.ApplyPixelSize(this, ClientToWindow(pixels));
        };
        ContentRendered += onRendered;
    }

    private void ApplyFrame()
    {
        WindowStyle = _source.Framed ? WindowStyle.SingleBorderWindow : WindowStyle.None;

        // Borderless: no invisible resize frame around the page (it would show what is behind the window in
        // captures and make the page smaller than the requested size); edges stay resizable.
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, _source.Framed
            ? null
            : new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
    }

    /// <summary>Window size whose client area is <paramref name="client"/> pixels (adds the frame when framed).</summary>
    private Size ClientToWindow(Size client)
    {
        if (!_source.Framed || ActualWidth <= 0 || _web.ActualWidth <= 0)
        {
            return client;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return new Size(client.Width + (ActualWidth - _web.ActualWidth) * dpi.DpiScaleX, client.Height + (ActualHeight - _web.ActualHeight) * dpi.DpiScaleY);
    }

    private void TrackSize()
    {
        if (WindowState != WindowState.Normal || _web.ActualWidth <= 0)
        {
            return;
        }

        // Borderless, the page fills the whole window, so the window size is what OBS captures (the WebView's
        // own layout size can lag behind by the resize border). Framed, only the page area counts.
        var dpi = VisualTreeHelper.GetDpi(this);
        var size = _source.Framed ? new Size(_web.ActualWidth, _web.ActualHeight) : new Size(ActualWidth, ActualHeight);
        _source.ReportPixelSize((int)Math.Round(size.Width * dpi.DpiScaleX), (int)Math.Round(size.Height * dpi.DpiScaleY));
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserSourceViewModel.Muted) when _web.CoreWebView2 is { } core:
                core.IsMuted = _source.Muted;
                break;
            case nameof(BrowserSourceViewModel.Zoom):
                _web.ZoomFactor = _source.Zoom;
                break;
            case nameof(BrowserSourceViewModel.Framed):
                ApplyFrame();
                break;
        }
    }

    private void OnNavigateRequested(object? sender, string url) => _web.CoreWebView2?.Navigate(url);

    private void OnReloadRequested(object? sender, EventArgs e) => _web.CoreWebView2?.Reload();

    private void OnBackRequested(object? sender, EventArgs e)
    {
        if (_web.CoreWebView2?.CanGoBack == true)
        {
            _web.CoreWebView2.GoBack();
        }
    }

    private void OnBringToFrontRequested(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnSizeRequested(object? sender, OutputSizeRequest request)
    {
        var applied = OutputWindow.ApplyPixelSize(this, ClientToWindow(request.Requested));
        var dpi = VisualTreeHelper.GetDpi(this);
        request.Applied = _source.Framed && _web.ActualWidth > 0
            ? new Size(Math.Round(_web.ActualWidth * dpi.DpiScaleX), Math.Round(_web.ActualHeight * dpi.DpiScaleY))
            : applied;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _source.PropertyChanged -= OnSourcePropertyChanged;
        _source.NavigateRequested -= OnNavigateRequested;
        _source.ReloadRequested -= OnReloadRequested;
        _source.BackRequested -= OnBackRequested;
        _source.BringToFrontRequested -= OnBringToFrontRequested;
        _source.SizeRequested -= OnSizeRequested;
        _web.Dispose();
    }
}

/// <summary>Owns the browser-source windows and checks the WebView2 runtime once.</summary>
public sealed class BrowserPresenter : IBrowserPresenter
{
    private readonly Dictionary<Guid, BrowserSourceWindow> _windows = [];

    public BrowserPresenter()
    {
        try
        {
            RuntimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            RuntimeVersion = null;
        }
    }

    public string? RuntimeVersion { get; }

    public void Show(BrowserSourceViewModel source)
    {
        if (_windows.ContainsKey(source.Id))
        {
            return;
        }

        var window = new BrowserSourceWindow(source);
        window.PlaceFromModel();
        window.Closed += (_, _) =>
        {
            if (_windows.Remove(source.Id))
            {
                source.CloseCommand?.Execute(null);
            }
        };
        _windows[source.Id] = window;
        window.Show();
    }

    public void Close(BrowserSourceViewModel source)
    {
        if (_windows.Remove(source.Id, out var window))
        {
            window.Close();
        }
    }
}
