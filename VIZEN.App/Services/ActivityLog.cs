using System.Collections.ObjectModel;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;

namespace VIZEN.App.Services;

/// <summary>
/// One activity entry. Stores resource keys + arguments rather than text, so the history is shown
/// in whatever language is currently selected.
/// </summary>
public sealed class ActivityEntry : ObservableObject
{
    private readonly string _titleKey;
    private readonly string _detailKey;
    private readonly object?[] _args;

    public ActivityEntry(DateTime timestamp, string glyph, string titleKey, string detailKey, object?[] args)
    {
        Timestamp = timestamp;
        Glyph = glyph;
        _titleKey = titleKey;
        _detailKey = detailKey;
        _args = args;
    }

    public DateTime Timestamp { get; }
    public string Glyph { get; }
    public string Title => Loc.T(_titleKey);
    public string Detail => Loc.F(_detailKey, _args);
    public string TimeText => Timestamp.ToString("HH:mm:ss");
}

/// <summary>
/// In-memory log of real events that happened during this session. Not persisted.
/// Must be used from the UI thread.
/// </summary>
public sealed class ActivityLog
{
    /// <summary>Detail key that shows its single argument verbatim (e.g. a window title or OS error).</summary>
    public const string RawDetail = "Fmt.Raw";

    private const int MaxEntries = 50;

    public ObservableCollection<ActivityEntry> Entries { get; } = [];

    /// <summary>Raised for every new entry (the shell shows it as a toast). UI thread.</summary>
    public event EventHandler<ActivityEntry>? EntryAdded;

    public void Add(string glyph, string titleKey, string detailKey, params object?[] args) =>
        Insert(new ActivityEntry(DateTime.Now, glyph, titleKey, detailKey, args), toast: true);

    /// <summary>Adds an entry to the history without a toast (for events the shell already shows another way).</summary>
    public void Record(string glyph, string titleKey, string detailKey, params object?[] args) =>
        Insert(new ActivityEntry(DateTime.Now, glyph, titleKey, detailKey, args), toast: false);

    private void Insert(ActivityEntry entry, bool toast)
    {
        Entries.Insert(0, entry);
        if (toast)
        {
            EntryAdded?.Invoke(this, entry);
        }

        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }
    }
}
