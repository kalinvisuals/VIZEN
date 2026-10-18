namespace VIZEN.App.Models;

public enum ScheduleMode
{
    /// <summary>Always visible; with several layers they rotate continuously.</summary>
    Permanent,

    /// <summary>Each layer is shown once (one after another), then the schedule finishes.</summary>
    Once,

    /// <summary>Every <see cref="MediaSchedule.IntervalSeconds"/> the next layer is shown for its duration.</summary>
    Repeat,
}

public enum ScheduleOrder
{
    Sequential,
    Random,
}

/// <summary>One layer in a schedule or sponsor playlist; <see cref="Seconds"/> overrides the schedule's duration.</summary>
public sealed class ScheduleEntry
{
    public Guid LayerId { get; set; }
    public double? Seconds { get; set; }
}

/// <summary>
/// When and how media layers appear: a single layer (e.g. "logo every 5 minutes for 10 s") or a sponsor
/// playlist (several layers rotating). Persisted in schedules.json.
/// </summary>
public sealed class MediaSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public ScheduleMode Mode { get; set; } = ScheduleMode.Repeat;
    public ScheduleOrder Order { get; set; } = ScheduleOrder.Sequential;

    /// <summary>Wait this long after starting before the first appearance.</summary>
    public double StartDelaySeconds { get; set; }

    /// <summary>How long each appearance lasts; 0 = the media's own length (a video or one GIF loop).</summary>
    public double VisibleSeconds { get; set; } = 10;

    /// <summary>Repeat mode: time from one appearance's start to the next.</summary>
    public double IntervalSeconds { get; set; } = 300;

    /// <summary>Optional daily window, "HH:mm". Outside it nothing is shown.</summary>
    public string? ActiveFrom { get; set; }

    public string? ActiveUntil { get; set; }

    /// <summary>Optional global shortcut that shows the next layer now (e.g. "Ctrl + Alt + 1").</summary>
    public string? Hotkey { get; set; }

    /// <summary>Start this schedule automatically when VIZEN opens.</summary>
    public bool AutoStart { get; set; }

    public List<ScheduleEntry> Entries { get; set; } = [];
}
