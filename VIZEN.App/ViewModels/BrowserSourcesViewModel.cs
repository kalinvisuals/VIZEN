using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>Opens and closes browser-source windows (implemented by the view layer).</summary>
public interface IBrowserPresenter
{
    /// <summary>Version of the installed WebView2 runtime, or null when it is missing.</summary>
    string? RuntimeVersion { get; }

    void Show(BrowserSourceViewModel source);

    void Close(BrowserSourceViewModel source);
}

/// <summary>
/// Browser page: web pages (YouTube live chat, Twitch, any site) rendered by WebView2 in VIZEN-owned windows.
/// Those windows are listed as sources, so cropping, overlays, OBS outputs and compositions all work on them.
/// The embedded browser has its own profile in VIZEN's data folder: nothing is copied from your other browsers.
/// </summary>
public sealed class BrowserSourcesViewModel : ObservableObject
{
    private const string FileName = "browsers.json";

    private readonly IBrowserPresenter _presenter;
    private readonly ActivityLog _activity;
    private readonly DispatcherTimer _saveTimer;
    private string _newAddress = string.Empty;
    private string? _errorMessage;

    private BrowserExtensionViewModel? _extension;

    /// <summary>The VIZEN Connector extension (tabs of Chrome / Edge / Brave), shown on this page.</summary>
    public BrowserExtensionViewModel? Extension
    {
        get => _extension;
        set => SetProperty(ref _extension, value);
    }

    public BrowserSourcesViewModel(IBrowserPresenter presenter, ActivityLog activity)
    {
        _presenter = presenter;
        _activity = activity;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();

        AddCommand = new RelayCommand(Add, () => IsRuntimeAvailable);

        var data = JsonStore.Load<BrowsersFile>(FileName, out var loadError);
        if (loadError is not null)
        {
            _activity.Add("", "Browser.LoadFailedList", ActivityLog.RawDetail, loadError);
        }

        foreach (var source in data?.Sources ?? [])
        {
            Create(source);
        }

        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    public ObservableCollection<BrowserSourceViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool IsRuntimeAvailable => _presenter.RuntimeVersion is not null;

    public string RuntimeText => _presenter.RuntimeVersion is { } version
        ? Loc.F("Browser.RuntimeOk", version)
        : Loc.T("Browser.RuntimeMissing");

    public string NewAddress
    {
        get => _newAddress;
        set => SetProperty(ref _newAddress, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string ProfileLocation => Path.Combine(JsonStore.DataDirectory, "WebView2");

    public ICommand AddCommand { get; }

    /// <summary>Opens browser sources marked "open when VIZEN starts".</summary>
    public void OpenAutomatic()
    {
        foreach (var source in Items.Where(s => s.AutoOpen && !s.IsOpen))
        {
            Open(source);
        }
    }

    public void Open(BrowserSourceViewModel source)
    {
        if (source.IsOpen || !IsRuntimeAvailable)
        {
            return;
        }

        _presenter.Show(source);
        source.IsOpen = true;
        _activity.Add("", "Browser.Opened", "Browser.OpenedDetail", source.WindowTitle, source.Source.Url);
    }

    public void Close(BrowserSourceViewModel source)
    {
        if (!source.IsOpen)
        {
            return;
        }

        source.IsOpen = false;
        _presenter.Close(source);
        _activity.Add("", "Browser.Closed", "Browser.ClosedDetail", source.WindowTitle);
    }

    public void CloseAll()
    {
        foreach (var source in Items)
        {
            Close(source);
        }
    }

    private void Add()
    {
        if (BrowserUrls.Normalize(NewAddress) is not { } url)
        {
            ErrorMessage = Loc.F("Browser.InvalidAddress", NewAddress);
            return;
        }

        ErrorMessage = null;
        var source = Create(new BrowserSource { Name = SuggestName(url), Url = url });
        NewAddress = string.Empty;
        RequestSave();
        _activity.Add("", "Browser.Added", "Browser.AddedDetail", source.Name, url);
        Open(source);
    }

    private BrowserSourceViewModel Create(BrowserSource model)
    {
        var source = new BrowserSourceViewModel(model, RequestSave);
        source.OpenCommand = new RelayCommand(() => Open(source), () => !source.IsOpen && IsRuntimeAvailable);
        source.CloseCommand = new RelayCommand(() => Close(source), () => source.IsOpen);
        source.ConfirmDeleteCommand = new RelayCommand(() => Delete(source));
        source.Report += (_, report) => _activity.Add("", report.TitleKey, report.DetailKey, report.Args);
        Items.Add(source);
        return source;
    }

    private void Delete(BrowserSourceViewModel source)
    {
        Close(source);
        Items.Remove(source);
        RequestSave();
        _activity.Add("", "Browser.Deleted", "Browser.DeletedDetail", source.Name);
    }

    /// <summary>"YouTube chat", "Twitch chat" or the site name, made unique.</summary>
    private string SuggestName(string url)
    {
        var name = url.Contains("youtube.com/live_chat", StringComparison.OrdinalIgnoreCase) ? Loc.T("Browser.NameYouTubeChat")
            : url.Contains("/popout/", StringComparison.OrdinalIgnoreCase) && url.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase) ? Loc.T("Browser.NameTwitchChat")
            : new Uri(url).Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
        var unique = name;
        for (var n = 2; Items.Any(i => i.Name == unique); n++)
        {
            unique = $"{name} {n}";
        }

        return unique;
    }

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            JsonStore.Save(FileName, new BrowsersFile { Sources = Items.Select(i => i.Source).ToList() });
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("Browser.SaveFailed", ex.Message);
        }
    }

    private sealed class BrowsersFile
    {
        public int Version { get; set; } = 1;
        public List<BrowserSource> Sources { get; set; } = [];
    }
}
