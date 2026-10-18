using System.Windows;
using System.Windows.Input;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>One browser source: its page, window size and state, and the commands the Browser page offers.</summary>
public sealed class BrowserSourceViewModel : ObservableObject
{
    private readonly Action _save;
    private string _addressText;
    private string? _pageTitle;
    private string? _currentUrl;
    private string? _error;
    private bool _isOpen;
    private bool _isLoading;
    private bool _isConfirmingDelete;

    public BrowserSourceViewModel(BrowserSource source, Action save)
    {
        Source = source;
        _save = save;
        _addressText = source.Url;

        GoCommand = new RelayCommand(Go);
        ReloadCommand = new RelayCommand(() => ReloadRequested?.Invoke(this, EventArgs.Empty), () => IsOpen);
        BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty), () => IsOpen);
        YouTubeChatCommand = new RelayCommand(() => Navigate(BrowserUrls.YouTubeChatUrl(YouTubeVideoId!)), () => YouTubeVideoId is not null);
        TwitchChatCommand = new RelayCommand(() => Navigate(BrowserUrls.TwitchChatUrl(TwitchChannel!)), () => TwitchChannel is not null);
        SizePresetCommand = new RelayCommand<string>(ApplySizePreset);
        BringToFrontCommand = new RelayCommand(() => BringToFrontRequested?.Invoke(this, EventArgs.Empty), () => IsOpen);
        DeleteCommand = new RelayCommand(() => IsConfirmingDelete = true);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
    }

    public BrowserSource Source { get; }
    public Guid Id => Source.Id;

    public string Name
    {
        get => Source.Name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Trim() != Source.Name)
            {
                Source.Name = value.Trim();
                OnPropertyChanged(nameof(WindowTitle));
                _save();
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Window title; it is how the window is found as a source and in OBS.</summary>
    public string WindowTitle => WindowEnumerationService.BrowserSourceTitlePrefix + Name;

    /// <summary>The address box (what the user types).</summary>
    public string AddressText
    {
        get => _addressText;
        set
        {
            if (SetProperty(ref _addressText, value))
            {
                OnPropertyChanged(nameof(YouTubeVideoId));
                OnPropertyChanged(nameof(TwitchChannel));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string? YouTubeVideoId => BrowserUrls.YouTubeVideoId(AddressText) ?? BrowserUrls.YouTubeVideoId(_currentUrl);

    public string? TwitchChannel => BrowserUrls.TwitchChannel(AddressText) ?? BrowserUrls.TwitchChannel(_currentUrl);

    public double Zoom
    {
        get => Source.Zoom;
        set
        {
            Source.Zoom = Math.Clamp(Math.Round(value, 2), 0.25, 4);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZoomText));
            _save();
        }
    }

    public string ZoomText => $"{Math.Round(Zoom * 100)}%";

    public bool Muted
    {
        get => Source.Muted;
        set
        {
            Source.Muted = value;
            OnPropertyChanged();
            _save();
        }
    }

    public bool Framed
    {
        get => Source.Framed;
        set
        {
            Source.Framed = value;
            OnPropertyChanged();
            _save();
        }
    }

    public bool AutoOpen
    {
        get => Source.AutoOpen;
        set
        {
            Source.AutoOpen = value;
            OnPropertyChanged();
            _save();
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        internal set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(StatusText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string StatusText => Error is { } error
        ? error
        : !IsOpen
            ? Loc.T("Browser.StatusClosed")
            : _isLoading
                ? Loc.T("Browser.StatusLoading")
                : Loc.F("Browser.StatusLoaded", _pageTitle ?? "—", Loc.F("Editor.PixelSize", Source.PixelWidth, Source.PixelHeight));

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set => SetProperty(ref _isConfirmingDelete, value);
    }

    // Set by the manager.
    public ICommand? OpenCommand { get; internal set; }
    public ICommand? CloseCommand { get; internal set; }
    public ICommand? ConfirmDeleteCommand { get; internal set; }

    public ICommand GoCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand YouTubeChatCommand { get; }
    public ICommand TwitchChatCommand { get; }
    public ICommand SizePresetCommand { get; }
    public ICommand BringToFrontCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    public event EventHandler<string>? NavigateRequested;
    public event EventHandler? ReloadRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? BringToFrontRequested;
    public event EventHandler<OutputSizeRequest>? SizeRequested;

    /// <summary>For the activity log: what happened (navigated, invalid address, resized…).</summary>
    public event EventHandler<(string TitleKey, string DetailKey, object?[] Args)>? Report;

    internal void ReportNavigationStarted()
    {
        _isLoading = true;
        Error = null;
        OnPropertyChanged(nameof(StatusText));
    }

    internal void ReportNavigationCompleted(bool success, string? url, string? title, string? errorStatus)
    {
        _isLoading = false;
        _currentUrl = url;
        _pageTitle = title;
        if (success && url is not null && url != Source.Url && BrowserUrls.Normalize(url) is { } normalized)
        {
            Source.Url = normalized;
            _save();
        }

        Error = success ? null : Loc.F("Browser.LoadFailed", errorStatus ?? "?");
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(YouTubeVideoId));
        OnPropertyChanged(nameof(TwitchChannel));
        CommandManager.InvalidateRequerySuggested();
    }

    internal void ReportTitle(string? title)
    {
        _pageTitle = title;
        OnPropertyChanged(nameof(StatusText));
    }

    internal void ReportError(string message) => Error = message;

    internal void ReportPixelSize(int width, int height)
    {
        if (Source.PixelWidth != width || Source.PixelHeight != height)
        {
            (Source.PixelWidth, Source.PixelHeight) = (width, height);
            OnPropertyChanged(nameof(StatusText));
            _save();
        }
    }

    internal void ReportPosition(Point position)
    {
        (Source.Left, Source.Top) = (position.X, position.Y);
        _save();
    }

    private void Go()
    {
        if (BrowserUrls.Normalize(AddressText) is { } url)
        {
            Navigate(url);
        }
        else
        {
            Error = Loc.F("Browser.InvalidAddress", AddressText);
        }
    }

    private void Navigate(string url)
    {
        Source.Url = url;
        AddressText = url;
        _save();
        if (IsOpen)
        {
            NavigateRequested?.Invoke(this, url);
        }
        else
        {
            OpenCommand?.Execute(null);
        }

        Report?.Invoke(this, ("Browser.Navigated", "Browser.NavigatedDetail", [Name, url]));
    }

    private void ApplySizePreset(string preset)
    {
        Size? size = preset switch
        {
            "chat" => new Size(400, 720),
            "720p" => new Size(1280, 720),
            "1080p" => new Size(1920, 1080),
            "square" => new Size(720, 720),
            _ => null,
        };
        if (size is not { } s)
        {
            return;
        }

        if (!IsOpen)
        {
            (Source.PixelWidth, Source.PixelHeight) = ((int)s.Width, (int)s.Height);
            OnPropertyChanged(nameof(StatusText));
            _save();
            return;
        }

        var request = new OutputSizeRequest(s);
        SizeRequested?.Invoke(this, request);
        var applied = request.Applied ?? s;
        Report?.Invoke(this, ("Activity.OutputResized", "Activity.OutputResizedDetail",
            [WindowTitle, Loc.F("Editor.PixelSize", (int)applied.Width, (int)applied.Height)]));
    }
}
