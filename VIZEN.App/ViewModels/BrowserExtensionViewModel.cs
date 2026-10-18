using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>One browser tab in VIZEN, with the actions the user can ask for.</summary>
public sealed class BrowserTabViewModel : ObservableObject
{
    private readonly BrowserExtensionViewModel _owner;

    public BrowserTabViewModel(BrowserTab tab, BrowserExtensionViewModel owner)
    {
        Tab = tab;
        _owner = owner;
        ActivateCommand = new RelayCommand(() => _owner.Activate(this));
        ToggleMuteCommand = new RelayCommand(() => _owner.ToggleMute(this));
        PopOutCommand = new RelayCommand(() => _owner.PopOut(this));
    }

    public BrowserTab Tab { get; private set; }

    public string Title => string.IsNullOrWhiteSpace(Tab.Title) ? Tab.Site : Tab.Title;

    /// <summary>"youtube.com · Google Chrome".</summary>
    public string Detail => string.IsNullOrEmpty(Tab.Site) ? BrowserTabs.DisplayName(Tab.Browser) : $"{Tab.Site} · {BrowserTabs.DisplayName(Tab.Browser)}";

    public string Glyph => SourceClassifier.Glyph(Tab.Category);

    public bool Audible => Tab.Audible;

    public bool Muted => Tab.Muted;

    public bool Active => Tab.Active;

    /// <summary>Playing sound or muted: shown in the mixer.</summary>
    public bool HasSound => Tab.Audible || Tab.Muted;

    public string MuteGlyph => Tab.Muted ? "" : "";

    public string MuteName => Loc.F(Tab.Muted ? "Tabs.UnmuteOf" : "Tabs.MuteOf", Title);

    public ICommand ActivateCommand { get; }

    public ICommand ToggleMuteCommand { get; }

    public ICommand PopOutCommand { get; }

    internal void Update(BrowserTab tab)
    {
        if (tab == Tab)
        {
            return;
        }

        Tab = tab;
        OnPropertyChanged(string.Empty); // everything may have changed
    }
}

/// <summary>
/// The VIZEN Connector extension: whether VIZEN is registered as its native host, which browsers are connected, and
/// their tabs (title and site only). Connecting (registering the host) happens only when the user presses the button;
/// the user adds the extension to the browser. Tabs are moved or changed only on the user's request.
/// </summary>
public sealed class BrowserExtensionViewModel : ObservableObject, IDisposable
{
    private readonly BrowserBridgeService _bridge;
    private readonly BrowserHostRegistration _registration;
    private readonly ActivityLog _activity;
    private readonly string _exePath;
    private string? _errorMessage;

    public BrowserExtensionViewModel(BrowserBridgeService bridge, BrowserHostRegistration registration, ActivityLog activity, string exePath, string extensionFolder)
    {
        _bridge = bridge;
        _registration = registration;
        _activity = activity;
        _exePath = exePath;
        ExtensionFolder = extensionFolder;
        ConnectCommand = new RelayCommand(Connect);
        DisconnectCommand = new RelayCommand(Disconnect);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        CopyChromeAddressCommand = new RelayCommand(() => Copy("chrome://extensions"));
        CopyEdgeAddressCommand = new RelayCommand(() => Copy("edge://extensions"));
        _bridge.Changed += (_, _) => Sync();
        _bridge.CommandFailed += (_, failure) => _activity.Add("", "Tabs.CommandFailed", "Tabs.CommandFailedDetail", failure.Error);
        if (IsRegistered)
        {
            RefreshRegistration();
            _bridge.Start();
        }
    }

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

    /// <summary>Raised after the tab list changed (UI thread).</summary>
    public event EventHandler? TabsChanged;

    public string ExtensionFolder { get; }

    public bool IsExtensionFolderPresent => Directory.Exists(ExtensionFolder);

    /// <summary>VIZEN is registered as the extension's host (the user pressed Connect).</summary>
    public bool IsRegistered => DemoMode.IsOn || _registration.IsRegistered(_exePath);

    public bool IsBrowserConnected => _bridge.ConnectedBrowsers.Count > 0;

    public string StatusText => !IsRegistered ? Loc.T("Tabs.StatusNotConnected")
        : !IsBrowserConnected ? Loc.T("Tabs.StatusWaiting")
        : Loc.F("Tabs.StatusConnected", string.Join(", ", _bridge.ConnectedBrowsers.Select(BrowserTabs.DisplayName)), Tabs.Count);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyChromeAddressCommand { get; }
    public ICommand CopyEdgeAddressCommand { get; }

    /// <summary>The tabs of a category (null = all) whose title or site contains <paramref name="search"/>.</summary>
    public IEnumerable<BrowserTabViewModel> Filter(SourceCategory? category, string? search) =>
        Tabs.Where(t => (category is null || t.Tab.Category == category)
            && (string.IsNullOrWhiteSpace(search)
                || t.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || t.Tab.Site.Contains(search, StringComparison.CurrentCultureIgnoreCase)));

    /// <summary>The tabs of a browser (by its executable key, e.g. "chrome.exe") that play sound or are muted.</summary>
    public IEnumerable<BrowserTabViewModel> SoundTabsOf(string processKey) =>
        Tabs.Where(t => t.HasSound && string.Equals(t.Tab.ProcessKey, processKey, StringComparison.OrdinalIgnoreCase));

    internal void Activate(BrowserTabViewModel tab) => _ = _bridge.ActivateAsync(tab.Tab);

    internal void ToggleMute(BrowserTabViewModel tab) => _ = _bridge.SetMutedAsync(tab.Tab, !tab.Tab.Muted);

    internal void PopOut(BrowserTabViewModel tab)
    {
        _ = _bridge.PopOutAsync(tab.Tab);
        _activity.Add("", "Tabs.PoppedOut", "Tabs.PoppedOutDetail", tab.Title);
    }

    private void Connect()
    {
        try
        {
            if (!DemoMode.IsOn)
            {
                _registration.Register(_exePath);
            }

            _bridge.Start();
            ErrorMessage = null;
            _activity.Add("", "Tabs.Connected", "Tabs.ConnectedDetail");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorMessage = Loc.F("Tabs.ConnectFailed", ex.Message);
        }

        RaiseStatus();
    }

    /// <summary>
    /// Rewrites the host manifest of an existing connection, so a newer VIZEN also lets in the extension IDs it knows
    /// (for example the store builds) without the user pressing Connect again.
    /// </summary>
    private void RefreshRegistration()
    {
        if (DemoMode.IsOn)
        {
            return;
        }

        try
        {
            _registration.Register(_exePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The connection that is already there keeps working for the IDs it lists.
        }
    }

    private void Disconnect()
    {
        try
        {
            _registration.Unregister();
            ErrorMessage = null;
            _activity.Add("", "Tabs.Disconnected", "Tabs.DisconnectedDetail");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorMessage = Loc.F("Tabs.ConnectFailed", ex.Message);
        }

        RaiseStatus();
    }

    private void OpenFolder()
    {
        if (IsExtensionFolderPresent)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExtensionFolder}\"") { UseShellExecute = true });
        }
    }

    private static void Copy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy; the address is also written on the page.
        }
    }

    /// <summary>Keeps one row per tab (rows reused), in the browsers' order.</summary>
    private void Sync()
    {
        var tabs = _bridge.Store.All;
        foreach (var row in Tabs.Where(r => !tabs.Any(t => SameTab(t, r.Tab))).ToList())
        {
            Tabs.Remove(row);
        }

        for (var i = 0; i < tabs.Count; i++)
        {
            var existing = Tabs.FirstOrDefault(r => SameTab(r.Tab, tabs[i]));
            if (existing is null)
            {
                Tabs.Insert(Math.Min(i, Tabs.Count), new BrowserTabViewModel(tabs[i], this));
            }
            else
            {
                existing.Update(tabs[i]);
                var index = Tabs.IndexOf(existing);
                if (index != i && i < Tabs.Count)
                {
                    Tabs.Move(index, i);
                }
            }
        }

        RaiseStatus();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameTab(BrowserTab a, BrowserTab b) => a.Browser == b.Browser && a.Id == b.Id;

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(IsRegistered));
        OnPropertyChanged(nameof(IsBrowserConnected));
        OnPropertyChanged(nameof(StatusText));
    }

    public void Dispose() => _bridge.Dispose();
}
