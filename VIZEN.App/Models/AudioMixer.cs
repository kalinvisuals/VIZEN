using System.IO;
using System.Windows.Media;

namespace VIZEN.App.Models;

/// <summary>
/// The sound of one app as the mixer sees it: all its Windows audio sessions together, like a row in the Windows
/// volume mixer. Volume and mute are the per-session ones (ISimpleAudioVolume): they never change the master volume.
/// </summary>
public interface IMixerApp
{
    /// <summary>Stable key used by audio profiles: the executable file name in lower case, or "system" for system sounds.</summary>
    string Key { get; }

    string AppName { get; }

    /// <summary>Full path of the executable, when Windows reveals it (null for system sounds).</summary>
    string? ExecutablePath { get; }

    bool IsSystemSounds { get; }

    /// <summary>Audio sessions behind this row (a browser usually plays every tab through one session).</summary>
    int SessionCount { get; }

    bool IsPlayingSound { get; }

    /// <summary>0..1, the loudest of the app's sessions right now (before its volume is applied).</summary>
    float Peak { get; }

    /// <summary>0..1, the level as last measured, without the meter's smooth fall-back (used by Audio Focus).</summary>
    float LevelNow { get; }

    float Volume { get; set; }

    bool Muted { get; set; }

    /// <summary>Sessions, volume or mute changed (also when changed outside VIZEN, e.g. in the Windows volume mixer).</summary>
    event EventHandler? Changed;

    /// <summary>Only the level meter changed (raised up to ~14 times a second while the mixer is visible).</summary>
    event EventHandler? PeakChanged;
}

/// <summary>The apps that currently have an audio session on any active output device.</summary>
public interface IMixerSource
{
    IReadOnlyList<IMixerApp> MixerApps { get; }

    /// <summary>Apps appeared or went away.</summary>
    event EventHandler? MixerAppsChanged;

    /// <summary>
    /// The mixer is on screen (page or floating window): sessions are watched and level meters read. When nobody
    /// looks, the mixer adds no timer of its own.
    /// </summary>
    void SetMixerActive(bool active);

    /// <summary>Re-reads the sessions now (after a device change or on request).</summary>
    void RescanNow();

    ImageSource? IconFor(IMixerApp app);

    /// <summary>Audio Focus is on: sessions and levels are watched even while the mixer is not on screen.</summary>
    void SetFocusActive(bool active);

    /// <summary>Raised on every level-meter tick (about every 70 ms) with the time since the previous tick.</summary>
    event EventHandler<double>? MeterTick;

    /// <summary>A default output device exists (its master volume can be shown).</summary>
    bool HasMaster { get; }

    /// <summary>Name of the default output device ("Speakers (Realtek)").</summary>
    string? MasterDeviceName { get; }

    /// <summary>
    /// Master volume of the default output device (the Windows volume, 0..1). Read live while the mixer is on screen,
    /// so changes made in Windows show up at once; set only by the mixer's own "Windows volume" control, never by an
    /// app's volume.
    /// </summary>
    float MasterVolume { get; set; }

    bool MasterMuted { get; set; }

    /// <summary>0..1, level of everything the default device plays.</summary>
    float MasterPeak { get; }

    /// <summary>Master volume, mute or the default device changed (also from Windows).</summary>
    event EventHandler? MasterChanged;

    event EventHandler? MasterPeakChanged;
}

public enum AudioProfileKind
{
    Custom,
    Gaming,
    Streaming,
    Meeting,
    Studio,
}

/// <summary>The saved volume and mute of one app in an audio profile.</summary>
public sealed class AudioProfileEntry
{
    /// <summary>Executable file name in lower case ("chrome.exe"), or "system".</summary>
    public string App { get; set; } = string.Empty;

    /// <summary>Display name when the profile was saved (for the "not running" list).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>0..1.</summary>
    public float Volume { get; set; } = 1f;

    public bool Muted { get; set; }
}

public sealed class AudioProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public AudioProfileKind Kind { get; set; }

    /// <summary>User-given name (custom profiles; the built-in ones are named in the interface language).</summary>
    public string Name { get; set; } = string.Empty;

    public List<AudioProfileEntry> Entries { get; set; } = [];
}

/// <summary>audio.json: the audio profiles and where the floating mixer was left.</summary>
public sealed class AudioMixerFile
{
    public List<AudioProfile>? Profiles { get; set; }

    /// <summary>Top-left of the floating mixer in device-independent units; null = default corner.</summary>
    public double? MixerLeft { get; set; }

    public double? MixerTop { get; set; }

    /// <summary>Size the user gave the floating mixer (device-independent units); null = automatic size.</summary>
    public double? MixerWidth { get; set; }

    public double? MixerHeight { get; set; }

    /// <summary>Smart Audio Focus rule.</summary>
    public AudioFocusSettings? Focus { get; set; }

    /// <summary>Opacity of the floating mixer, 0.3..1 (null = fully opaque).</summary>
    public double? MixerOpacity { get; set; }
}

/// <summary>What applying a profile did.</summary>
public sealed record AudioProfileResult(int Applied, IReadOnlyList<string> NotRunning);

/// <summary>Saving and applying audio profiles (no Windows calls: works on <see cref="IMixerApp"/>).</summary>
public static class AudioProfiles
{
    public const string SystemKey = "system";

    /// <summary>The profile key of an executable: its file name in lower case (survives app updates and moved folders).</summary>
    public static string KeyFor(string? executablePath) =>
        string.IsNullOrEmpty(executablePath) ? string.Empty : Path.GetFileName(executablePath).ToLowerInvariant();

    /// <summary>The four built-in profiles, empty until the user saves a mix into them.</summary>
    public static List<AudioProfile> Defaults() =>
    [
        new() { Kind = AudioProfileKind.Gaming },
        new() { Kind = AudioProfileKind.Streaming },
        new() { Kind = AudioProfileKind.Meeting },
        new() { Kind = AudioProfileKind.Studio },
    ];

    /// <summary>
    /// Built-in profiles that are missing are added back; entries without an app key, duplicate apps and out-of-range
    /// volumes from a hand-edited file are cleaned up.
    /// </summary>
    public static List<AudioProfile> Sanitize(IEnumerable<AudioProfile?>? loaded)
    {
        var result = new List<AudioProfile>();
        foreach (var profile in loaded ?? [])
        {
            if (profile is null || (profile.Kind == AudioProfileKind.Custom && string.IsNullOrWhiteSpace(profile.Name))
                || (profile.Kind != AudioProfileKind.Custom && result.Any(p => p.Kind == profile.Kind)))
            {
                continue;
            }

            if (profile.Id == Guid.Empty || result.Any(p => p.Id == profile.Id))
            {
                profile.Id = Guid.NewGuid();
            }

            profile.Name = profile.Name?.Trim() ?? string.Empty;
            profile.Entries = (profile.Entries ?? [])
                .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.App))
                .GroupBy(e => e.App.ToLowerInvariant())
                .Select(g => g.Last())
                .Select(e => new AudioProfileEntry
                {
                    App = e.App.ToLowerInvariant(),
                    Name = string.IsNullOrWhiteSpace(e.Name) ? e.App : e.Name,
                    Volume = float.IsFinite(e.Volume) ? Math.Clamp(e.Volume, 0f, 1f) : 1f,
                    Muted = e.Muted,
                })
                .ToList();
            result.Add(profile);
        }

        foreach (var builtIn in Defaults())
        {
            if (!result.Any(p => p.Kind == builtIn.Kind))
            {
                result.Insert(Math.Min((int)builtIn.Kind - 1, result.Count), builtIn);
            }
        }

        return result;
    }

    /// <summary>The current volume and mute of every app with sound, as profile entries.</summary>
    public static List<AudioProfileEntry> Snapshot(IEnumerable<IMixerApp> apps) =>
        apps.Where(a => !string.IsNullOrEmpty(a.Key))
            .GroupBy(a => a.Key)
            .Select(g => g.First())
            .Select(a => new AudioProfileEntry { App = a.Key, Name = a.AppName, Volume = Math.Clamp(a.Volume, 0f, 1f), Muted = a.Muted })
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Sets each saved app that has sound now to its saved volume and mute. Apps in the profile that are not playing
    /// through Windows right now are listed, not waited for. Apps not in the profile are left as they are.
    /// </summary>
    public static AudioProfileResult Apply(AudioProfile profile, IEnumerable<IMixerApp> apps)
    {
        var byKey = apps.Where(a => !string.IsNullOrEmpty(a.Key)).GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.ToList());
        var applied = 0;
        var notRunning = new List<string>();
        foreach (var entry in profile.Entries)
        {
            if (!byKey.TryGetValue(entry.App, out var targets))
            {
                notRunning.Add(entry.Name);
                continue;
            }

            foreach (var app in targets)
            {
                app.Volume = Math.Clamp(entry.Volume, 0f, 1f);
                app.Muted = entry.Muted;
            }

            applied++;
        }

        return new AudioProfileResult(applied, notRunning);
    }
}
