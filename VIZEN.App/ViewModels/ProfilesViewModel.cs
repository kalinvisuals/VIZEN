using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

public sealed class ProfilesViewModel : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly WindowEnumerationService _enumerator;
    private readonly Func<IEnumerable<WindowSource>> _cameraSources;
    private readonly ActivityLog _activity;
    private string _newProfileName = string.Empty;
    private string? _statusMessage;
    private string? _errorMessage;
    private bool _isApplying;

    public ProfilesViewModel(
        ProfileStore store, OverlaysViewModel overlays, OutputsViewModel outputs, WindowEnumerationService enumerator,
        Func<IEnumerable<WindowSource>> cameraSources, ActivityLog activity)
    {
        _store = store;
        _enumerator = enumerator;
        _cameraSources = cameraSources;
        _activity = activity;
        Overlays = overlays;
        Outputs = outputs;

        var saved = _store.Load(out var loadError);
        foreach (var profile in saved.OrderByDescending(p => p.UpdatedAt))
        {
            Profiles.Add(CreateItem(profile));
        }

        if (loadError is not null)
        {
            ErrorMessage = loadError;
            _activity.Add("", "Activity.ProfilesLoadFailed", ActivityLog.RawDetail, loadError);
        }

        SaveCurrentCommand = new RelayCommand(SaveCurrent, () => OpenItemCount > 0);
        Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasProfiles));
        };
        Overlays.Overlays.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SaveHint));
        Outputs.Outputs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SaveHint));
    }

    public OverlaysViewModel Overlays { get; }

    public OutputsViewModel Outputs { get; }

    /// <summary>Overlays + OBS output windows that a profile would capture.</summary>
    private int OpenItemCount => Overlays.Count + Outputs.Count;

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    public int Count => Profiles.Count;

    public bool HasProfiles => Profiles.Count > 0;

    public ICommand SaveCurrentCommand { get; }

    public string NewProfileName
    {
        get => _newProfileName;
        set => SetProperty(ref _newProfileName, value);
    }

    public string SaveHint => OpenItemCount switch
    {
        0 => Loc.T("Profiles.SaveHintNone"),
        1 => Loc.T("Profiles.SaveHintOne"),
        var n => Loc.F("Profiles.SaveHintMany", n),
    };

    /// <summary>Result of the last action. Built when the action runs, so it stays in that language.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsApplying
    {
        get => _isApplying;
        private set => SetProperty(ref _isApplying, value);
    }

    public string StorageLocation => Path.Combine(JsonStore.DataDirectory, "profiles.json");

    private ProfileItemViewModel CreateItem(OverlayProfile profile)
        => new(profile, ApplyAsync, Update, Delete, () => !IsApplying, () => OpenItemCount > 0);

    private void SaveCurrent()
    {
        var name = UniqueName(string.IsNullOrWhiteSpace(NewProfileName)
            ? Loc.F("Profiles.DefaultName", Profiles.Count + 1)
            : NewProfileName.Trim());
        var profile = new OverlayProfile { Name = name, Overlays = CaptureCurrentLayout() };

        Profiles.Insert(0, CreateItem(profile));
        if (Persist())
        {
            NewProfileName = string.Empty;
            StatusMessage = Loc.F("Profiles.Saved", name, DescribeCount(profile.Overlays));
            _activity.Add("", "Activity.ProfileSaved", ActivityLog.RawDetail, StatusMessage);
        }
    }

    private void Update(ProfileItemViewModel item)
    {
        item.Model.Overlays = CaptureCurrentLayout();
        item.Model.UpdatedAt = DateTime.Now;
        item.Refresh();
        if (Persist())
        {
            StatusMessage = Loc.F("Profiles.Updated", item.Name, DescribeCount(item.Model.Overlays));
            _activity.Add("", "Activity.ProfileUpdated", ActivityLog.RawDetail, StatusMessage);
        }
    }

    private void Delete(ProfileItemViewModel item)
    {
        Profiles.Remove(item);
        if (Persist())
        {
            StatusMessage = Loc.F("Profiles.Deleted", item.Name);
            _activity.Add("", "Activity.ProfileDeleted", ActivityLog.RawDetail, StatusMessage);
        }
    }

    /// <summary>Replaces the open overlays with the profile's, matching each to a current window.</summary>
    private async Task ApplyAsync(ProfileItemViewModel item)
    {
        if (IsApplying)
        {
            return;
        }

        IsApplying = true;
        ErrorMessage = null;
        StatusMessage = Loc.F("Profiles.Applying", item.Name);
        try
        {
            var windows = await Task.Run(() => _enumerator.EnumerateWindows(
                new WindowEnumerationOptions(ExcludeOwnProcess: true, IncludeMinimized: true)));
            // Cameras are matched like windows (their identity is "camera:<id>").
            var matches = ProfileMatcher.Match(item.Model.Overlays, windows.Concat(_cameraSources()).ToList());

            Overlays.CloseAll();
            Outputs.CloseAll();
            var opened = 0;
            var notFound = new List<string>();
            var failed = new List<string>();
            var appOnly = new List<string>();

            foreach (var match in matches)
            {
                if (match.Window is null)
                {
                    notFound.Add(match.Layout.ProcessName);
                    continue;
                }

                var created = match.Layout.IsOutput
                    ? Outputs.CreateOutput(match.Window, layout: match.Layout)
                    : Overlays.CreateOverlay(match.Window, match.Layout);
                if (!created)
                {
                    failed.Add(match.Window.ProcessName);
                    continue;
                }

                opened++;
                if (match.Quality == MatchQuality.AppOnly)
                {
                    appOnly.Add(match.Window.ProcessName);
                }
            }

            var message = Loc.F("Profiles.Applied", item.Name, opened, matches.Count);
            if (notFound.Count > 0)
            {
                message += " " + Loc.F("Profiles.NotRunning", string.Join(", ", notFound.Distinct()));
            }

            if (failed.Count > 0)
            {
                message += " " + Loc.F("Profiles.CouldNotCapture", string.Join(", ", failed.Distinct()));
            }

            if (appOnly.Count > 0)
            {
                message += " " + Loc.F("Profiles.MatchedByApp", string.Join(", ", appOnly.Distinct()));
            }

            StatusMessage = message;
            _activity.Add("", "Activity.ProfileApplied", ActivityLog.RawDetail, message);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Profiles.ApplyFailed", item.Name, ex.Message);
            StatusMessage = null;
        }
        finally
        {
            IsApplying = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private List<OverlayLayout> CaptureCurrentLayout() => Overlays.Overlays
        .Select(o => new OverlayLayout
        {
            ProcessName = o.Source.ProcessName,
            ExecutablePath = o.Source.ExecutablePath,
            WindowTitle = o.Source.Title,
            ClassName = o.Source.ClassName,
            Left = o.Bounds.IsEmpty ? 0 : o.Bounds.Left,
            Top = o.Bounds.IsEmpty ? 0 : o.Bounds.Top,
            Width = o.Bounds.IsEmpty ? 0 : o.Bounds.Width,
            Height = o.Bounds.IsEmpty ? 0 : o.Bounds.Height,
            Opacity = o.Opacity,
            ClickThrough = o.IsClickThrough,
            RegionX = o.Viewbox.X,
            RegionY = o.Viewbox.Y,
            RegionWidth = o.Viewbox.Width,
            RegionHeight = o.Viewbox.Height,
            RegionName = o.RegionName,
        })
        .Concat(Outputs.Outputs.Select(o => new OverlayLayout
        {
            Kind = OverlayLayout.KindOutput,
            ProcessName = o.Source.ProcessName,
            ExecutablePath = o.Source.ExecutablePath,
            WindowTitle = o.Source.Title,
            ClassName = o.Source.ClassName,
            Left = o.Bounds.IsEmpty ? 0 : o.Bounds.Left,
            Top = o.Bounds.IsEmpty ? 0 : o.Bounds.Top,
            Width = o.Bounds.IsEmpty ? 0 : o.Bounds.Width,
            Height = o.Bounds.IsEmpty ? 0 : o.Bounds.Height,
            RegionX = o.Viewbox.X,
            RegionY = o.Viewbox.Y,
            RegionWidth = o.Viewbox.Width,
            RegionHeight = o.Viewbox.Height,
            RegionName = o.RegionName,
            OutputBackground = o.Background.ToString(),
            OutputFit = o.Fit.ToString(),
        }))
        .ToList();

    private bool Persist()
    {
        try
        {
            _store.Save(Profiles.Select(p => p.Model));
            ErrorMessage = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("Profiles.SaveFailed", ex.Message);
            StatusMessage = null;
            return false;
        }
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var n = 2; Profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.CurrentCultureIgnoreCase)); n++)
        {
            candidate = $"{name} ({n})";
        }

        return candidate;
    }

    /// <summary>E.g. "4 overlays and 4 OBS outputs" — counts each kind of window in a layout.</summary>
    internal static string DescribeCount(IReadOnlyCollection<OverlayLayout> layouts)
    {
        var outputs = layouts.Count(l => l.IsOutput);
        var overlays = layouts.Count - outputs;
        return (overlays, outputs) switch
        {
            (_, 0) => overlays == 1 ? Loc.T("Profiles.OneOverlay") : Loc.F("Profiles.ManyOverlays", overlays),
            (0, _) => Loc.F("Profiles.CountOutputs", outputs),
            _ => Loc.F("Profiles.CountBoth", overlays, outputs),
        };
    }
}

public sealed class ProfileItemViewModel : ObservableObject
{
    private bool _isConfirmingDelete;

    public ProfileItemViewModel(
        OverlayProfile model,
        Func<ProfileItemViewModel, Task> apply,
        Action<ProfileItemViewModel> update,
        Action<ProfileItemViewModel> delete,
        Func<bool> canApply,
        Func<bool> canUpdate)
    {
        Model = model;
        ApplyCommand = new AsyncRelayCommand(() => apply(this), canApply);
        UpdateCommand = new RelayCommand(() => update(this), canUpdate);
        DeleteCommand = new RelayCommand(() => IsConfirmingDelete = true);
        ConfirmDeleteCommand = new RelayCommand(() => delete(this));
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
    }

    public OverlayProfile Model { get; }

    public string Name => Model.Name;

    public string Summary => Loc.F("Profiles.Summary",
        ProfilesViewModel.DescribeCount(Model.Overlays),
        Model.UpdatedAt.ToString("d MMM yyyy, HH:mm"));

    public string SourcesText => Model.Overlays.Count == 0
        ? Loc.T("Profiles.NoOverlays")
        : string.Join("  ·  ", Model.Overlays.Select(o => o.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase));

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        private set => SetProperty(ref _isConfirmingDelete, value);
    }

    public ICommand ApplyCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SourcesText));
    }
}
