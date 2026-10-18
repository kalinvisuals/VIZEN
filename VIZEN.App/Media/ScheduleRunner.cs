using System.Globalization;
using VIZEN.App.Models;

namespace VIZEN.App.Media;

public enum RunnerState
{
    Stopped,
    Waiting,
    Showing,
    Paused,
    Finished,
}

/// <summary>Show or hide one media layer.</summary>
public readonly record struct LayerChange(Guid LayerId, bool Show);

/// <summary>A planned appearance, for the preview list.</summary>
public readonly record struct PlannedShow(DateTime At, Guid LayerId, TimeSpan? Duration);

/// <summary>
/// Runs one <see cref="MediaSchedule"/>: a small state machine driven by the current time. It does not touch
/// windows or timers — every call returns the layer changes to apply — so it is deterministic and testable.
/// Schedules run independently; each owns its own runner.
/// </summary>
public sealed class ScheduleRunner
{
    private static readonly TimeSpan DefaultMediaLength = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(1);

    private readonly MediaSchedule _schedule;
    private readonly Func<Guid, TimeSpan?> _mediaLength;
    private readonly Random _random;
    private readonly List<int> _bag = [];
    private int _sequenceIndex;
    private int _shownCount;
    private int _lastIndex = -1;
    private DateTime _lastShowStart;
    private RunnerState? _stateAfterManual;

    /// <param name="mediaLength">Length of a layer's media (video duration, one GIF loop), or null if unknown/still image.</param>
    public ScheduleRunner(MediaSchedule schedule, Func<Guid, TimeSpan?> mediaLength, Random? random = null)
    {
        _schedule = schedule;
        _mediaLength = mediaLength;
        _random = random ?? new Random();
    }

    public RunnerState State { get; private set; } = RunnerState.Stopped;

    /// <summary>Layer currently shown by this schedule.</summary>
    public Guid? Current { get; private set; }

    /// <summary>When the next show/hide happens (null = nothing planned, e.g. a permanent logo).</summary>
    public DateTime? NextChangeAt { get; private set; }

    public bool IsRunning => State is RunnerState.Waiting or RunnerState.Showing;

    public IReadOnlyList<LayerChange> Start(DateTime now)
    {
        var changes = HideCurrent();
        _bag.Clear();
        _sequenceIndex = 0;
        _shownCount = 0;
        _lastIndex = -1;
        _stateAfterManual = null;
        if (_schedule.Entries.Count == 0)
        {
            State = RunnerState.Finished;
            NextChangeAt = null;
            return changes;
        }

        State = RunnerState.Waiting;
        NextChangeAt = now + TimeSpan.FromSeconds(Math.Max(0, _schedule.StartDelaySeconds));
        changes.AddRange(Tick(now));
        return changes;
    }

    public IReadOnlyList<LayerChange> Tick(DateTime now)
    {
        var changes = new List<LayerChange>();
        if (State is not (RunnerState.Waiting or RunnerState.Showing))
        {
            return changes;
        }

        if (_stateAfterManual is null && !IsInWindow(now))
        {
            if (State == RunnerState.Showing)
            {
                changes.AddRange(HideCurrent());
                State = RunnerState.Waiting;
                NextChangeAt = now;
            }

            return changes;
        }

        // Several steps can fall due at once (e.g. a hide immediately followed by the next show).
        for (var guard = 0; guard < 8; guard++)
        {
            if (State == RunnerState.Waiting && NextChangeAt is { } showAt && now >= showAt)
            {
                ShowNext(now, changes, manual: false);
            }
            else if (State == RunnerState.Showing && NextChangeAt is { } hideAt && now >= hideAt)
            {
                EndAppearance(now, changes);
            }
            else
            {
                break;
            }
        }

        return changes;
    }

    /// <summary>Manual or hotkey trigger: show the next layer now. A stopped schedule goes back to stopped afterwards.</summary>
    public IReadOnlyList<LayerChange> Trigger(DateTime now)
    {
        var changes = new List<LayerChange>();
        if (_schedule.Entries.Count == 0 || (_schedule.Mode == ScheduleMode.Permanent && _schedule.Entries.Count == 1 && State == RunnerState.Showing))
        {
            return changes;
        }

        if (State is RunnerState.Stopped or RunnerState.Paused or RunnerState.Finished)
        {
            _stateAfterManual = State;
        }

        ShowNext(now, changes, manual: true);
        return changes;
    }

    public IReadOnlyList<LayerChange> Pause()
    {
        var changes = HideCurrent();
        if (State is RunnerState.Waiting or RunnerState.Showing)
        {
            State = RunnerState.Paused;
        }

        NextChangeAt = null;
        return changes;
    }

    public IReadOnlyList<LayerChange> Resume(DateTime now)
    {
        if (State != RunnerState.Paused)
        {
            return [];
        }

        State = RunnerState.Waiting;
        NextChangeAt = now;
        return Tick(now);
    }

    public IReadOnlyList<LayerChange> Stop()
    {
        var changes = HideCurrent();
        State = RunnerState.Stopped;
        NextChangeAt = null;
        _stateAfterManual = null;
        return changes;
    }

    /// <summary>A layer's video/GIF finished: ends the appearance when its duration is "the media's length".</summary>
    public IReadOnlyList<LayerChange> OnMediaEnded(Guid layerId, DateTime now)
    {
        if (State != RunnerState.Showing || Current != layerId || EntryFor(layerId) is not { } entry || SecondsOf(entry) > 0)
        {
            return [];
        }

        NextChangeAt = now;
        return Tick(now);
    }

    /// <summary>When this runner next needs a <see cref="Tick"/> (accounting for the daily window).</summary>
    public DateTime? NextWakeUp(DateTime now)
    {
        if (!IsRunning || NextChangeAt is not { } next)
        {
            return null;
        }

        var due = next < now ? now : next;
        return IsInWindow(due) ? due : NextWindowStart(due);
    }

    /// <summary>Simulates the schedule from <paramref name="now"/> and lists the next <paramref name="count"/> appearances.</summary>
    public static IReadOnlyList<PlannedShow> Preview(MediaSchedule schedule, Func<Guid, TimeSpan?> mediaLength, DateTime now, int count, int seed = 1)
    {
        var runner = new ScheduleRunner(schedule, mediaLength, new Random(seed));
        var shows = new List<PlannedShow>();
        var time = now;
        Collect(runner.Start(time));
        for (var steps = 0; steps < count * 4 + 8 && shows.Count < count; steps++)
        {
            if (runner.NextWakeUp(time) is not { } wake)
            {
                break;
            }

            time = wake;
            Collect(runner.Tick(time));
        }

        return shows;

        void Collect(IReadOnlyList<LayerChange> changes)
        {
            foreach (var change in changes.Where(c => c.Show))
            {
                shows.Add(new PlannedShow(time, change.LayerId, runner.NextChangeAt is { } end ? end - time : null));
            }
        }
    }

    // ----- Internals -----

    private void ShowNext(DateTime now, List<LayerChange> changes, bool manual)
    {
        if (!manual && _schedule.Mode == ScheduleMode.Once && _shownCount >= _schedule.Entries.Count)
        {
            changes.AddRange(HideCurrent());
            State = RunnerState.Finished;
            NextChangeAt = null;
            return;
        }

        var entry = PickNext();
        _shownCount++;
        if (Current is { } previous && previous != entry.LayerId)
        {
            changes.Add(new LayerChange(previous, false));
        }

        if (Current != entry.LayerId)
        {
            changes.Add(new LayerChange(entry.LayerId, true));
        }

        Current = entry.LayerId;
        State = RunnerState.Showing;
        _lastShowStart = now;
        var permanentSingle = !manual && _schedule.Mode == ScheduleMode.Permanent && _schedule.Entries.Count == 1;
        NextChangeAt = permanentSingle ? null : now + DurationOf(entry);
    }

    private void EndAppearance(DateTime now, List<LayerChange> changes)
    {
        if (_stateAfterManual is { } back)
        {
            // A manual trigger on a stopped/paused/finished schedule shows one layer, then returns.
            _stateAfterManual = null;
            changes.AddRange(HideCurrent());
            State = back;
            NextChangeAt = null;
            return;
        }

        switch (_schedule.Mode)
        {
            case ScheduleMode.Permanent:
                ShowNext(now, changes, manual: false);
                break;

            case ScheduleMode.Once:
                if (_shownCount < _schedule.Entries.Count)
                {
                    ShowNext(now, changes, manual: false);
                }
                else
                {
                    changes.AddRange(HideCurrent());
                    State = RunnerState.Finished;
                    NextChangeAt = null;
                }

                break;

            default:
                changes.AddRange(HideCurrent());
                State = RunnerState.Waiting;
                var next = _lastShowStart + TimeSpan.FromSeconds(Math.Max(1, _schedule.IntervalSeconds));
                NextChangeAt = next > now ? next : now;
                break;
        }
    }

    private List<LayerChange> HideCurrent()
    {
        var changes = new List<LayerChange>();
        if (Current is { } current)
        {
            changes.Add(new LayerChange(current, false));
            Current = null;
        }

        return changes;
    }

    private ScheduleEntry PickNext()
    {
        var entries = _schedule.Entries;
        int index;
        if (_schedule.Order == ScheduleOrder.Random && entries.Count > 1)
        {
            _bag.RemoveAll(i => i >= entries.Count);
            if (_bag.Count == 0)
            {
                // Shuffle-bag: every layer once per round, and no immediate repeat across rounds.
                _bag.AddRange(Enumerable.Range(0, entries.Count).OrderBy(_ => _random.Next()));
                if (_bag[0] == _lastIndex)
                {
                    (_bag[0], _bag[^1]) = (_bag[^1], _bag[0]);
                }
            }

            index = _bag[0];
            _bag.RemoveAt(0);
        }
        else
        {
            index = _sequenceIndex % entries.Count;
            _sequenceIndex++;
        }

        _lastIndex = index;
        return entries[index];
    }

    private ScheduleEntry? EntryFor(Guid layerId) => _schedule.Entries.FirstOrDefault(e => e.LayerId == layerId);

    private double SecondsOf(ScheduleEntry entry) => entry.Seconds is > 0 ? entry.Seconds.Value : _schedule.VisibleSeconds;

    private TimeSpan DurationOf(ScheduleEntry entry)
    {
        var seconds = SecondsOf(entry);
        var duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : _mediaLength(entry.LayerId) ?? DefaultMediaLength;
        return duration < MinimumDuration ? MinimumDuration : duration;
    }

    // ----- Daily window -----

    public static TimeSpan? ParseTime(string? text) =>
        TimeSpan.TryParseExact(text?.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;

    public bool IsInWindow(DateTime now)
    {
        var from = ParseTime(_schedule.ActiveFrom);
        var until = ParseTime(_schedule.ActiveUntil);
        var t = now.TimeOfDay;
        return (from, until) switch
        {
            (null, null) => true,
            ({ } f, null) => t >= f,
            (null, { } u) => t < u,
            ({ } f, { } u) when f <= u => t >= f && t < u,
            ({ } f, { } u) => t >= f || t < u, // crosses midnight
        };
    }

    private DateTime NextWindowStart(DateTime after)
    {
        var from = ParseTime(_schedule.ActiveFrom) ?? TimeSpan.Zero;
        var candidate = after.Date + from;
        return candidate > after ? candidate : candidate.AddDays(1);
    }
}
