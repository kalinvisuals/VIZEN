namespace VIZEN.App.Models;

/// <summary>One app chosen for an Audio Focus rule (profile key + the name shown when it is not running).</summary>
public sealed class AudioFocusApp
{
    public string App { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Smart Audio Focus settings (saved in audio.json): when the priority app produces sound above the sensitivity
/// threshold, the affected apps are lowered by <see cref="DuckPercent"/> and brought back after it has been quiet for
/// <see cref="HoldMs"/>. This reacts to the sound level only; it does not recognise speech.
/// </summary>
public sealed class AudioFocusSettings
{
    public const int MinDuck = 10, MaxDuck = 100, MinAttack = 50, MaxAttack = 2000, MinHold = 0, MaxHold = 5000, MinRelease = 100, MaxRelease = 5000;

    public bool Enabled { get; set; }

    public AudioFocusApp? Priority { get; set; }

    /// <summary>Apps that are lowered (ignored when <see cref="AllOthers"/> is on).</summary>
    public List<AudioFocusApp> Ducked { get; set; } = [];

    /// <summary>Lower every app except the priority app (and system sounds).</summary>
    public bool AllOthers { get; set; } = true;

    /// <summary>How much the affected apps are lowered, in % of their own volume (60 = down to 40 %).</summary>
    public int DuckPercent { get; set; } = 60;

    /// <summary>1..100: higher reacts to quieter sound.</summary>
    public int Sensitivity { get; set; } = 60;

    /// <summary>Time to go down once the priority app is heard.</summary>
    public int AttackMs { get; set; } = 250;

    /// <summary>How long the others stay down after the priority app went quiet.</summary>
    public int HoldMs { get; set; } = 800;

    /// <summary>Time to come back up after the hold.</summary>
    public int ReleaseMs { get; set; } = 1200;

    /// <summary>The level (0..1 peak) from which the priority app counts as active, for this sensitivity.</summary>
    public static float ThresholdFor(int sensitivity)
    {
        var s = Math.Clamp(sensitivity, 1, 100) / 100f;
        return 0.004f + 0.25f * (1 - s) * (1 - s);
    }

    public void Sanitize()
    {
        DuckPercent = Math.Clamp(DuckPercent, MinDuck, MaxDuck);
        Sensitivity = Math.Clamp(Sensitivity, 1, 100);
        AttackMs = Math.Clamp(AttackMs, MinAttack, MaxAttack);
        HoldMs = Math.Clamp(HoldMs, MinHold, MaxHold);
        ReleaseMs = Math.Clamp(ReleaseMs, MinRelease, MaxRelease);
        Ducked = (Ducked ?? []).Where(d => d is not null && !string.IsNullOrWhiteSpace(d.App))
            .GroupBy(d => d.App.ToLowerInvariant()).Select(g => g.First()).ToList();
        foreach (var d in Ducked)
        {
            d.App = d.App.ToLowerInvariant();
        }

        if (Priority is { App: { Length: > 0 } } priority)
        {
            priority.App = priority.App.ToLowerInvariant();
        }
        else
        {
            Priority = null;
        }
    }
}

/// <summary>
/// The ducking envelope: a gain factor (1 = normal, 1 − duck = lowered) that goes down over the attack time while the
/// priority app is active, stays down for the hold time after it went quiet, then comes back over the release time.
/// Pure logic, driven by elapsed time.
/// </summary>
public sealed class AudioDucker
{
    private double _sinceActiveMs = double.MaxValue;

    /// <summary>Current gain applied to the affected apps (0..1).</summary>
    public double Factor { get; private set; } = 1;

    /// <summary>The priority app is heard right now.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The affected apps are not at their normal level (going down, held, or coming back).</summary>
    public bool IsDucking => Factor < 0.999;

    public void Reset()
    {
        Factor = 1;
        IsActive = false;
        _sinceActiveMs = double.MaxValue;
    }

    /// <summary>Advances by <paramref name="elapsedMs"/> with the priority app's current level.</summary>
    public void Step(double elapsedMs, float level, AudioFocusSettings settings)
    {
        var target = 1 - Math.Clamp(settings.DuckPercent, AudioFocusSettings.MinDuck, AudioFocusSettings.MaxDuck) / 100.0;
        IsActive = level >= AudioFocusSettings.ThresholdFor(settings.Sensitivity);
        _sinceActiveMs = IsActive ? 0 : Math.Min(double.MaxValue / 2, _sinceActiveMs + elapsedMs);

        if (IsActive || _sinceActiveMs <= settings.HoldMs)
        {
            if (Factor > target)
            {
                // Linear in time: full depth in AttackMs.
                Factor = Math.Max(target, Factor - (1 - target) * elapsedMs / Math.Max(1, settings.AttackMs));
            }
            else if (Factor < target)
            {
                Factor = target; // the depth was made smaller while ducked
            }
        }
        else if (Factor < 1)
        {
            Factor = Math.Min(1, Factor + (1 - target) * elapsedMs / Math.Max(1, settings.ReleaseMs));
        }
    }
}

/// <summary>A lowered app's own volume, for the restore file.</summary>
public sealed class AudioFocusRestoreEntry
{
    public string App { get; set; } = string.Empty;

    public float Volume { get; set; }
}

/// <summary>
/// Applies Audio Focus to the apps: remembers each affected app's own ("base") volume, sets base × factor while
/// ducking and puts the base back when done. A volume the user sets through VIZEN while an app is lowered becomes its
/// new base. Changes made by other programs during a duck (for example Discord's own attenuation) are not adopted, so
/// the user's volume is never lost; outside changes while not ducking are simply the app's normal volume.
/// </summary>
public sealed class AudioFocusEngine
{
    private readonly Dictionary<IMixerApp, (float Base, float Set)> _lowered = [];
    private readonly AudioDucker _ducker = new();

    public AudioDucker Ducker => _ducker;

    /// <summary>Raised when the set of lowered apps or their bases changed (the caller saves the restore file).</summary>
    public event EventHandler? LoweredChanged;

    /// <summary>The app's own volume while VIZEN holds it lowered (null when it is not lowered).</summary>
    public float? BaseFor(IMixerApp app) => _lowered.TryGetValue(app, out var entry) ? entry.Base : null;

    public bool IsLowered(IMixerApp app) => _lowered.ContainsKey(app);

    public IReadOnlyCollection<IMixerApp> Lowered => _lowered.Keys;

    /// <summary>The priority app (first match by key), or null when it is not playing through Windows now.</summary>
    public static IMixerApp? PriorityApp(AudioFocusSettings settings, IEnumerable<IMixerApp> apps) =>
        settings.Priority is { } p ? apps.FirstOrDefault(a => a.Key == p.App) : null;

    /// <summary>The apps that are lowered while the priority app is active.</summary>
    public static IEnumerable<IMixerApp> AffectedApps(AudioFocusSettings settings, IEnumerable<IMixerApp> apps)
    {
        var priority = settings.Priority?.App;
        return settings.AllOthers
            ? apps.Where(a => a.Key != priority && !a.IsSystemSounds)
            : apps.Where(a => a.Key != priority && settings.Ducked.Any(d => d.App == a.Key));
    }

    /// <summary>One step: reads the priority level, advances the envelope and sets the affected apps' volumes.</summary>
    public void Step(double elapsedMs, AudioFocusSettings settings, IReadOnlyList<IMixerApp> apps)
    {
        if (!settings.Enabled || settings.Priority is null)
        {
            RestoreAll();
            return;
        }

        var level = PriorityApp(settings, apps) is { } priority ? priority.LevelNow : 0f;
        _ducker.Step(elapsedMs, level, settings);

        var affected = AffectedApps(settings, apps).ToHashSet();
        var changed = false;

        // Apps that are no longer affected, or that went away: give them back their volume, forget them.
        foreach (var app in _lowered.Keys.Where(a => !affected.Contains(a) || !apps.Contains(a)).ToList())
        {
            if (apps.Contains(app))
            {
                SafeSet(app, _lowered[app].Base);
            }

            _lowered.Remove(app);
            changed = true;
        }

        if (!_ducker.IsDucking)
        {
            if (_lowered.Count > 0)
            {
                foreach (var (app, entry) in _lowered)
                {
                    SafeSet(app, entry.Base);
                }

                _lowered.Clear();
                changed = true;
            }
        }
        else
        {
            foreach (var app in affected)
            {
                if (!_lowered.TryGetValue(app, out var entry))
                {
                    entry = (app.Volume, app.Volume);   // its own volume when the duck starts
                    changed = true;
                }

                var want = (float)Math.Clamp(entry.Base * _ducker.Factor, 0, 1);
                if (Math.Abs(want - entry.Set) > 0.003f || Math.Abs(app.Volume - want) > 0.01f)
                {
                    SafeSet(app, want);   // also undoes an outside change made during the duck
                }

                _lowered[app] = (entry.Base, want);
            }
        }

        if (changed)
        {
            LoweredChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The user moved an app's volume in VIZEN. True when the app is lowered right now: the value becomes its base and
    /// the app is set to base × current factor (it stays lowered until the duck ends).
    /// </summary>
    public bool UserSetVolume(IMixerApp app, float volume)
    {
        if (!_lowered.TryGetValue(app, out _))
        {
            return false;
        }

        var baseVolume = Math.Clamp(volume, 0f, 1f);
        var want = (float)Math.Clamp(baseVolume * _ducker.Factor, 0, 1);
        SafeSet(app, want);
        _lowered[app] = (baseVolume, want);
        LoweredChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Puts every lowered app back to its own volume now (Restore Volumes, Audio Focus off, VIZEN exiting).</summary>
    public void RestoreAll()
    {
        _ducker.Reset();
        if (_lowered.Count == 0)
        {
            return;
        }

        foreach (var (app, entry) in _lowered)
        {
            SafeSet(app, entry.Base);
        }

        _lowered.Clear();
        LoweredChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The lowered apps and their own volumes (for the restore file).</summary>
    public List<AudioFocusRestoreEntry> RestoreEntries() =>
        _lowered.Select(p => new AudioFocusRestoreEntry { App = p.Key.Key, Volume = p.Value.Base }).ToList();

    /// <summary>
    /// After an unexpected exit: puts back the volumes saved in the restore file for the apps that have sound now.
    /// Returns how many apps were restored.
    /// </summary>
    public static int RestoreFromFile(IEnumerable<AudioFocusRestoreEntry> entries, IEnumerable<IMixerApp> apps)
    {
        var restored = 0;
        var list = apps.ToList();
        foreach (var entry in entries)
        {
            foreach (var app in list.Where(a => a.Key == entry.App))
            {
                SafeSet(app, Math.Clamp(entry.Volume, 0f, 1f));
                restored++;
            }
        }

        return restored;
    }

    private static void SafeSet(IMixerApp app, float volume)
    {
        try
        {
            app.Volume = volume;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The app's session went away; the next step forgets it.
        }
    }
}
