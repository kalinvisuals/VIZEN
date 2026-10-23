using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>
/// Settings → Updates. Checks in the background (shortly after startup, then every 6 hours, only when allowed), shows
/// what is available and installs only after the user confirms: VIZEN is never closed or restarted on its own.
/// Store and App Installer copies are left to Windows.
/// </summary>
public sealed class UpdatesViewModel : ObservableObject, IDisposable
{
    public enum State { Idle, Checking, UpToDate, Available, AvailableUnverified, Downloading, ReadyToInstall, Failed, Managed, Unsupported }

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly AppSettings _settings;
    private readonly ActivityLog _activity;
    private readonly UpdateChecker _checker;
    private readonly UpdateDownloader _downloader;
    private readonly Func<int> _activeVideo;
    private readonly Action _exitForInstall;
    private readonly AppVersion _current;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _work;
    private UpdateCheckResult? _result;
    private string? _installerPath;
    private State _state = State.Idle;
    private double _progress;
    private bool _confirming;
    private bool? _managedAvailable;
    private string? _error;
    private DateTime? _lastChecked;
    private string? _announcedVersion;
    private bool _notInstalled;

    public UpdatesViewModel(AppSettings settings, ActivityLog activity, UpdateChecker checker, UpdateDownloader downloader,
        UpdateChannel channel, string currentVersion, Func<int> activeVideo, Action exitForInstall)
    {
        _settings = settings;
        _activity = activity;
        _checker = checker;
        _downloader = downloader;
        _activeVideo = activeVideo;
        _exitForInstall = exitForInstall;
        Channel = channel;
        _current = AppVersion.TryParse(currentVersion, out var v) ? v : default;
        CurrentVersion = _current.ToString();
        if (_current != default)
        {
            _downloader.RemoveInstalled(_current); // the installer of the update that brought this version
        }
        _state = channel switch
        {
            UpdateChannel.Store or UpdateChannel.AppInstaller => State.Managed,
            UpdateChannel.Sideloaded => State.Unsupported,
            _ => State.Idle,
        };

        CheckCommand = new RelayCommand(() => _ = CheckAsync(manual: true), () => !IsBusy);
        DownloadCommand = new RelayCommand(() => _ = DownloadAsync(thenConfirm: true), () => _state == State.Available && CanInstallHere);
        InstallCommand = new RelayCommand(() => Confirming = true, () => _state == State.ReadyToInstall);
        ConfirmInstallCommand = new RelayCommand(ConfirmInstall);
        CancelInstallCommand = new RelayCommand(() => Confirming = false);
        CancelDownloadCommand = new RelayCommand(() => _work?.Cancel(), () => _state == State.Downloading);
        OpenReleasePageCommand = new RelayCommand(OpenReleasePage, () => ReleasePage is not null);
        OpenStoreCommand = new RelayCommand(() => UpdateChannels.OpenManagedUpdate(Channel), () => Channel is UpdateChannel.Store or UpdateChannel.AppInstaller);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FirstCheckDelay };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (_settings.AutoCheckUpdates)
            {
                _ = CheckAsync(manual: false);
            }
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.Language) or "")
            {
                OnPropertyChanged(string.Empty);
            }
        };
    }

    public UpdateChannel Channel { get; }

    public string CurrentVersion { get; }

    public string? LatestVersion => _result?.Version;

    public string ChannelText => Loc.T(Channel switch
    {
        UpdateChannel.Store => "Updates.ChannelStore",
        UpdateChannel.AppInstaller => "Updates.ChannelAppInstaller",
        UpdateChannel.Sideloaded => "Updates.ChannelSideloaded",
        UpdateChannel.Unmanaged => "Updates.ChannelUnmanaged",
        _ => "Updates.ChannelInstaller",
    });

    /// <summary>Only the classic installer copy installs updates itself.</summary>
    public bool CanInstallHere => Channel == UpdateChannel.Installer;

    public bool IsBusy => _state is State.Checking or State.Downloading;

    public bool IsDownloading => _state == State.Downloading;

    public double Progress => _progress * 100;

    public string ProgressText => Loc.F("Updates.Downloading", (int)Math.Round(_progress * 100));

    public string StatusText => _state switch
    {
        State.Checking => Loc.T("Updates.Checking"),
        State.UpToDate => Loc.F("Updates.UpToDate", CurrentVersion),
        State.Available when !CanInstallHere => Loc.F("Updates.AvailableElsewhere", LatestVersion),
        State.Available => Loc.F("Updates.Available", LatestVersion),
        State.AvailableUnverified => Loc.F("Updates.AvailableUnverified", LatestVersion),
        State.Downloading => ProgressText,
        State.ReadyToInstall => Loc.F("Updates.Ready", LatestVersion),
        State.Failed => _notInstalled ? Loc.T("Updates.NotInstalled") : Loc.T("Updates.Failed"),
        State.Managed when Channel == UpdateChannel.Store => _managedAvailable == true ? Loc.T("Updates.StoreAvailable") : Loc.T("Updates.StoreManaged"),
        State.Managed => _managedAvailable == true ? Loc.T("Updates.AppInstallerAvailable") : Loc.T("Updates.AppInstallerManaged"),
        State.Unsupported => Loc.T("Updates.Sideloaded"),
        _ => _settings.AutoCheckUpdates ? Loc.T("Updates.WillCheck") : Loc.T("Updates.NotChecked"),
    };

    public bool IsUpdateAvailable => _state is State.Available or State.AvailableUnverified or State.Downloading or State.ReadyToInstall;

    public string? ErrorText => _state == State.Failed ? _error : null;

    public string? WarningText => _result?.Warning is { } w ? Loc.F("Updates.SourcesDiffer", w) : null;

    public string? SourceText => _result?.Source is { } s ? Loc.F("Updates.Source", s) : null;

    public string? LastCheckedText => _lastChecked is { } t ? Loc.F("Updates.LastChecked", t.ToString("t")) : null;

    public string? ReleaseNotes => _result?.Manifest?.NotesFor(_settings.Language) is { Length: > 0 } notes ? notes : null;

    public string? SizeText => _result?.Manifest?.Installer?.Size is > 0 and var size ? Loc.F("Updates.Size", (size / 1048576.0).ToString("0.0")) : null;

    public string? ReleasePage => _result?.ReleasePage;

    public bool ShowDownload => _state == State.Available && CanInstallHere;

    public bool ShowInstall => _state == State.ReadyToInstall && !_confirming;

    public bool ShowOpenPage => ReleasePage is not null && (_state == State.AvailableUnverified || (_state == State.Available && !CanInstallHere));

    public bool ShowOpenStore => Channel is UpdateChannel.Store or UpdateChannel.AppInstaller;

    public bool Confirming
    {
        get => _confirming;
        private set
        {
            if (SetProperty(ref _confirming, value))
            {
                OnPropertyChanged(nameof(ShowInstall));
                OnPropertyChanged(nameof(ConfirmText));
            }
        }
    }

    public string ConfirmText => _activeVideo() is var n and > 0 ? Loc.F("Updates.ConfirmWithVideo", n) : Loc.T("Updates.Confirm");

    public ICommand CheckCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand ConfirmInstallCommand { get; }
    public ICommand CancelInstallCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand OpenReleasePageCommand { get; }
    public ICommand OpenStoreCommand { get; }

    /// <summary>Starts the background schedule (first check shortly after startup, so it never slows the start).</summary>
    public void Start() => _timer.Start();

    public async Task CheckAsync(bool manual)
    {
        if (IsBusy)
        {
            return;
        }

        if (Channel is UpdateChannel.Store or UpdateChannel.AppInstaller)
        {
            _managedAvailable = Channel == UpdateChannel.Store
                ? await UpdateChannels.StoreUpdateAvailableAsync()
                : await UpdateChannels.AppInstallerUpdateAvailableAsync();
            _lastChecked = DateTime.Now;
            Raise();
            return;
        }

        if (Channel == UpdateChannel.Sideloaded)
        {
            return;
        }

        SetState(State.Checking);
        _work = new CancellationTokenSource();
        try
        {
            _result = await Task.Run(() => _checker.CheckAsync(_current, _work.Token));
            _lastChecked = DateTime.Now;
            _error = _result.Error;
            SetState(_result.Status switch
            {
                UpdateCheckStatus.Available => State.Available,
                UpdateCheckStatus.AvailableUnverified => State.AvailableUnverified,
                UpdateCheckStatus.UpToDate => State.UpToDate,
                _ => State.Failed,
            });

            if (_state is State.Available or State.AvailableUnverified && _announcedVersion != _result.Version)
            {
                _announcedVersion = _result.Version;
                _activity.Add("", "Updates.FoundTitle", "Updates.FoundDetail", _result.Version);
            }

            if (_state == State.Available && CanInstallHere && _settings.AutoDownloadUpdates)
            {
                await DownloadAsync(thenConfirm: false);
            }
        }
        catch (OperationCanceledException)
        {
            SetState(State.Idle);
        }
    }

    private async Task DownloadAsync(bool thenConfirm)
    {
        if (_result?.Manifest is not { } manifest || !CanInstallHere || IsBusy)
        {
            return;
        }

        SetState(State.Downloading);
        _progress = 0;
        _work = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            _progress = p;
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(StatusText));
        });
        try
        {
            _installerPath = await Task.Run(() => _downloader.DownloadAsync(manifest, progress, _work.Token));
            SetState(State.ReadyToInstall);
            Confirming = thenConfirm;
        }
        catch (UpdateException ex)
        {
            _error = Loc.F("Updates.VerifyFailed", ex.Message);
            SetState(State.Failed);
        }
        catch (OperationCanceledException)
        {
            SetState(State.Available); // the partial file is kept and resumed next time
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error = ex.Message;
            SetState(State.Failed);
        }
    }

    private void ConfirmInstall()
    {
        Confirming = false;
        if (_installerPath is null || _result?.Manifest is not { } manifest)
        {
            return;
        }

        // Checked again right before running: the file could have changed since the download.
        if (UpdateDownloader.Verify(_installerPath, manifest, checkVersionInfo: true) is { } problem)
        {
            _error = Loc.F("Updates.VerifyFailed", problem);
            _installerPath = null;
            SetState(State.Failed);
            return;
        }

        if (!UpdateChannels.StartInstaller(_installerPath))
        {
            _activity.Add("", "Updates.InstallCancelled", "Updates.InstallCancelledDetail");
            return;
        }

        _exitForInstall(); // the installer replaces the program files and starts VIZEN again
    }

    private void OpenReleasePage()
    {
        if (ReleasePage is { } page && page.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
        }
    }

    private void SetState(State state)
    {
        _notInstalled = state == State.Failed && _state is State.Downloading or State.ReadyToInstall;
        _state = state;
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(string.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    public void Dispose()
    {
        _timer.Stop();
        _work?.Cancel();
    }
}
