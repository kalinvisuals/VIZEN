using System.ComponentModel;
using System.Windows;
using VIZEN.App.Localization;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;
using VIZEN.App.Views;

namespace VIZEN.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;
    private bool _trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unhandled error still ends the app as before, but leaves its details in crash.log in the data folder
        // (%LOCALAPPDATA%\VIZEN), so a crash on another PC can be diagnosed without the Windows event log.
        DispatcherUnhandledException += (_, args) => WriteCrashLog(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);

        // "VIZEN.App.exe --test-pattern-native 60 1": a game-like (Direct3D, vsync) test source, in its own process.
        if (NativeTestPatternWindow.FromArguments(e.Args) is { } nativePattern)
        {
            MainWindow = nativePattern;
            nativePattern.Show();
            return;
        }

        // "VIZEN.App.exe --test-pattern 60 1": only the test source window, in its own process (Performance page).
        if (TestPatternWindow.FromArguments(e.Args) is { } pattern)
        {
            MainWindow = pattern;
            pattern.Show();
            return;
        }

        // VIZEN_UI=software renders the controls on the CPU (saves ~40 MB RAM and ~40 MB GPU memory at idle on an integrated GPU).
        // Not the default: WPF's software renderer left stale parts of the previous page on screen after navigation and
        // resizing (seen in screenshots), and a correct picture matters more than the memory.
        if (Environment.GetEnvironmentVariable("VIZEN_UI") == "software")
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        var monitors = new MonitorProvider();
        _viewModel = new MainViewModel(new OverlayPresenter(), new OutputPresenter(), new MediaPresenter(), new MediaFilePicker(),
            new CompositionPresenter(), new BrowserPresenter(), new NotePresenter(), new SmartDrawPresenter(monitors), monitors, new MixerPresenter());
        var settings = _viewModel.Settings.Settings;

        _window = new MainWindow { DataContext = _viewModel };
        MainWindow = _window;
        _window.Closing += OnMainWindowClosing;
        // Closing the main window ends VIZEN (ShutdownMode): note windows closed by WPF then keep their "on screen" state.
        _window.Closed += (_, _) => NotePresenter.BeginExit();
        SessionEnding += (_, _) => NotePresenter.BeginExit();

        _tray = new TrayIcon(ShowMainWindow, _viewModel.ToggleAllLayers, ExitFromTray);
        settings.PropertyChanged += OnSettingsChanged;
        TopmostKeeper.ExclusiveFullscreenBlocked += OnExclusiveFullscreenBlocked;

        // Started by Windows at sign-in (--tray): stay in the notification area, or start minimized.
        var startedAtSignIn = e.Args.Contains(StartupRegistration.TrayArgument, StringComparer.OrdinalIgnoreCase);
        if (startedAtSignIn && settings.RunInTray)
        {
            _tray.Visible = true;
        }
        else
        {
            if (startedAtSignIn)
            {
                _window.WindowState = WindowState.Minimized;
            }

            _window.Show();
            _tray.Visible = settings.RunInTray;
        }

        // Capture setup and initial window scan; errors are surfaced in the UI by the view models.
        _ = _viewModel.InitializeAsync();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.RunInTray) && _tray is not null && _viewModel is not null)
        {
            _tray.Visible = _viewModel.Settings.Settings.RunInTray || _window?.IsVisible == false;
        }
    }

    /// <summary>
    /// Overlays were held back over an exclusive-fullscreen game. The banner waits in the main window; nothing is put on
    /// screen over the game (that would minimize it). When VIZEN is only in the notification area, Windows' own
    /// notification is used instead, which Windows itself holds until the game leaves fullscreen.
    /// </summary>
    private void OnExclusiveFullscreenBlocked(object? sender, EventArgs e)
    {
        if (_viewModel?.ReportExclusiveFullscreen() == true && _window?.IsVisible != true && _tray?.Visible == true)
        {
            _tray.Notify(Loc.T("Shell.FullscreenTipTitle"), Loc.T("Activity.ExclusiveFullscreenDetail"));
        }
    }

    /// <summary>With "Keep running in the notification area", closing hides VIZEN instead of quitting.</summary>
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _viewModel?.Settings.Settings.RunInTray != true || _window is null)
        {
            return;
        }

        e.Cancel = true;
        _window.Hide();
        _tray!.Visible = true;
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.Notify("VIZEN", Loc.T("Tray.StillRunning"));
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _tray!.Visible = _viewModel?.Settings.Settings.RunInTray == true;
    }

    private void ExitFromTray()
    {
        _exiting = true;
        NotePresenter.BeginExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TopmostKeeper.ExclusiveFullscreenBlocked -= OnExclusiveFullscreenBlocked;
        _tray?.Dispose();

        // Stops all captures and releases the Direct3D device.
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            var version = typeof(App).Assembly.GetName().Version;
            System.IO.Directory.CreateDirectory(JsonStore.DataDirectory);
            System.IO.File.AppendAllText(System.IO.Path.Combine(JsonStore.DataDirectory, "crash.log"),
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  VIZEN {version}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done while crashing.
        }
    }
}
