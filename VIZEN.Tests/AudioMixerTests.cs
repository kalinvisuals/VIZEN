using System.ComponentModel;
using System.Windows.Media;
using VIZEN.App.Models;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.Tests;

/// <summary>Smart Audio Mixer logic with fake audio apps (no Windows audio calls) and a temporary data folder.</summary>
[Collection(DataFolderCollection.Name)]
public class AudioMixerTests
{
    internal sealed class FakeApp(string key, string name, float volume = 1f, bool muted = false, bool system = false) : IMixerApp
    {
        private float _volume = volume;
        private bool _muted = muted;

        public string Key { get; } = key;
        public string AppName { get; } = name;
        public string? ExecutablePath => IsSystemSounds ? null : @"C:\Apps\" + Key;
        public bool IsSystemSounds { get; } = system;
        public int SessionCount { get; set; } = 1;
        public bool IsPlayingSound { get; set; } = true;
        public float Peak { get; private set; }
        public float LevelNow { get; set; }
        public int VolumeWrites { get; private set; }

        public float Volume
        {
            get => _volume;
            set { _volume = value; VolumeWrites++; }
        }

        public bool Muted
        {
            get => _muted;
            set => _muted = value;
        }

        public event EventHandler? Changed;
        public event EventHandler? PeakChanged;

        /// <summary>Simulates a change made outside VIZEN (Windows volume mixer, the app itself).</summary>
        public void ChangeOutside(float volume, bool muted)
        {
            _volume = volume;
            _muted = muted;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetPeak(float peak)
        {
            Peak = peak;
            PeakChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal sealed class FakeSource : IMixerSource
    {
        private List<IMixerApp> _apps = [];

        public IReadOnlyList<IMixerApp> MixerApps => _apps;
        public List<bool> ActiveCalls { get; } = [];
        public bool IsActive => ActiveCalls.Count > 0 && ActiveCalls[^1];
        public int Rescans { get; private set; }

        public event EventHandler? MixerAppsChanged;

        public void Set(params IMixerApp[] apps)
        {
            _apps = [.. apps];
            MixerAppsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetMixerActive(bool active) => ActiveCalls.Add(active);
        public void RescanNow() => Rescans++;
        public List<bool> FocusCalls { get; } = [];
        public void SetFocusActive(bool active) => FocusCalls.Add(active);
        public event EventHandler<double>? MeterTick;
        public void Tick(double ms) => MeterTick?.Invoke(this, ms);
        public ImageSource? IconFor(IMixerApp app) => null;

        public bool HasMaster => true;
        public string? MasterDeviceName => "Speakers";
        public float MasterVolume { get; set; } = 0.8f;
        public bool MasterMuted { get; set; }
        public float MasterPeak => 0f;
        public event EventHandler? MasterChanged;
        public event EventHandler? MasterPeakChanged;

        /// <summary>Simulates the Windows volume being changed in Windows (taskbar, keyboard keys).</summary>
        public void ChangeMasterInWindows(float volume, bool muted)
        {
            MasterVolume = volume;
            MasterMuted = muted;
            MasterChanged?.Invoke(this, EventArgs.Empty);
            MasterPeakChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal sealed class FakePresenter : IMixerPresenter
    {
        public List<string> Calls { get; } = [];
        public void Show(AudioMixerViewModel mixer) => Calls.Add("show");
        public void Hide() => Calls.Add("hide");
        public void Close() => Calls.Add("close");
    }

    private static AudioMixerViewModel NewMixer(FakeSource source, FakePresenter? presenter = null, ActivityLog? log = null, bool freshFolder = true)
    {
        if (freshFolder)
        {
            DataFolderCollection.UseTemporaryFolder();
        }

        return new AudioMixerViewModel(source, presenter ?? new FakePresenter(), log ?? new ActivityLog());
    }

    [Fact]
    public void AppsAppearAndDisappearAndRowsAreReused()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        Assert.False(mixer.HasApps);

        var chrome = new FakeApp("chrome.exe", "Google Chrome");
        var discord = new FakeApp("discord.exe", "Discord");
        source.Set(chrome, discord);
        Assert.Equal(["Google Chrome", "Discord"], mixer.Apps.Select(a => a.Name));
        var chromeRow = mixer.Apps[0];

        var spotify = new FakeApp("spotify.exe", "Spotify");
        source.Set(discord, chrome, spotify);  // a new app, and a new order
        Assert.Equal(["Discord", "Google Chrome", "Spotify"], mixer.Apps.Select(a => a.Name));
        Assert.Same(chromeRow, mixer.Apps[1]);

        source.Set(spotify);                   // two apps closed
        Assert.Equal(["Spotify"], mixer.Apps.Select(a => a.Name));
        Assert.Equal(Loc("Mixer.Summary", 1), mixer.SummaryText);
    }

    [Fact]
    public void VolumeAndMuteChangeOnlyThatApp()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        var chrome = new FakeApp("chrome.exe", "Google Chrome");
        var game = new FakeApp("cs2.exe", "Counter-Strike 2");
        source.Set(chrome, game);

        mixer.Apps[0].VolumePercent = 30;
        mixer.Apps[0].ToggleMuteCommand.Execute(null);

        Assert.Equal(0.30f, chrome.Volume, 3);
        Assert.True(chrome.Muted);
        Assert.Equal(1f, game.Volume);
        Assert.Equal(0, game.VolumeWrites);
        Assert.False(game.Muted);
        Assert.Equal("30%", mixer.Apps[0].VolumeText);

        mixer.Apps[0].VolumePercent = 150;   // clamped
        Assert.Equal(1f, chrome.Volume);
    }

    [Fact]
    public void ChangesMadeOutsideVizenShowUp()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        var app = new FakeApp("spotify.exe", "Spotify");
        source.Set(app);
        var row = mixer.Apps[0];
        var changed = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        app.ChangeOutside(0.25f, muted: true);
        Assert.Contains(nameof(MixerAppViewModel.VolumePercent), changed);
        Assert.Contains(nameof(MixerAppViewModel.Muted), changed);
        Assert.Equal(25, row.VolumePercent);
        Assert.True(row.Muted);

        changed.Clear();
        app.SetPeak(0.5f);                   // the level meter alone
        Assert.Equal([nameof(MixerAppViewModel.Peak)], changed);
        Assert.Equal(0.5, row.Peak, 3);
    }

    [Fact]
    public void SessionsAreWatchedOnlyWhileTheMixerIsOnScreen()
    {
        var source = new FakeSource();
        var presenter = new FakePresenter();
        var mixer = NewMixer(source, presenter);
        Assert.False(source.IsActive);

        mixer.IsPageVisible = true;
        Assert.True(source.IsActive);
        mixer.ToggleFloating();              // floating shown as well
        mixer.IsPageVisible = false;
        Assert.True(source.IsActive);         // still watched: the floating mixer is on screen
        mixer.ToggleFloating();              // hotkey again: hidden
        Assert.False(source.IsActive);
        Assert.Equal(["show", "hide"], presenter.Calls);

        mixer.Shutdown();
        Assert.Equal("close", presenter.Calls[^1]);
        Assert.False(source.IsActive);
    }

    [Fact]
    public void ProfileSavesTheMixAndApplyRestoresIt()
    {
        var source = new FakeSource();
        var log = new ActivityLog();
        var mixer = NewMixer(source, log: log);
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.3f);
        var discord = new FakeApp("discord.exe", "Discord", 0.8f);
        var system = new FakeApp(AudioProfiles.SystemKey, "System sounds", 0.5f, muted: true, system: true);
        source.Set(chrome, discord, system);
        var gaming = mixer.Profiles.Single(p => p.Profile.Kind == AudioProfileKind.Gaming);
        Assert.True(gaming.IsEmpty);

        gaming.SaveCommand.Execute(null);
        Assert.Equal(3, gaming.Profile.Entries.Count);

        chrome.Volume = 1f;
        discord.Muted = true;
        system.Muted = false;
        var spotify = new FakeApp("spotify.exe", "Spotify", 0.6f);
        source.Set(chrome, discord, system, spotify);
        gaming.ApplyCommand.Execute(null);

        Assert.Equal(0.3f, chrome.Volume, 3);
        Assert.False(discord.Muted);
        Assert.Equal(0.8f, discord.Volume, 3);
        Assert.True(system.Muted);
        Assert.Equal(0.6f, spotify.Volume, 3);   // not in the profile: left alone
        Assert.Equal(0, spotify.VolumeWrites);
    }

    [Fact]
    public void ActiveProfileIsHighlightedUntilAVolumeIsChangedByHand()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.5f);
        source.Set(chrome);
        var gaming = mixer.Profiles[0];
        var studio = mixer.Profiles[3];

        gaming.SaveCommand.Execute(null);
        Assert.True(gaming.IsActive);
        studio.SaveCommand.Execute(null);
        Assert.False(gaming.IsActive);           // only one active profile
        Assert.True(studio.IsActive);
        gaming.ApplyCommand.Execute(null);
        Assert.True(gaming.IsActive);
        Assert.Same(gaming, mixer.ActiveProfile);

        chrome.ChangeOutside(0.2f, false);       // a change outside VIZEN does not clear it
        Assert.True(gaming.IsActive);
        mixer.Apps[0].VolumePercent = 70;        // moved by hand: the mix no longer matches the profile
        Assert.False(gaming.IsActive);
        Assert.Null(mixer.ActiveProfile);
    }

    [Fact]
    public void SelectedProfileStaysWhenVolumesChangeSoSaveHasATarget()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.5f);
        source.Set(chrome);
        Assert.Same(mixer.Profiles[0], mixer.SelectedProfile);   // something is selected from the start

        var meeting = mixer.Profiles[2];
        meeting.ApplyCommand.Execute(null);                       // empty: nothing applied, but selected
        Assert.Same(meeting, mixer.SelectedProfile);
        Assert.Equal(Loc("Mixer.ProfileEmpty"), mixer.SelectedStateText);

        mixer.Apps[0].VolumePercent = 30;
        mixer.SelectedProfile!.SaveCommand.Execute(null);
        Assert.Equal(0.3f, meeting.Profile.Entries[0].Volume, 3);
        Assert.Equal(Loc("Mixer.StateApplied"), mixer.SelectedStateText);

        mixer.Apps[0].VolumePercent = 80;                          // changed by hand: still selected, no longer applied
        Assert.Same(meeting, mixer.SelectedProfile);
        Assert.True(meeting.IsSelected);
        Assert.False(meeting.IsActive);
        Assert.Equal(Loc("Mixer.StateChanged"), mixer.SelectedStateText);
    }

    [Fact]
    public void WindowsVolumeFollowsWindowsAndAppVolumesNeverTouchIt()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        var chrome = new FakeApp("chrome.exe", "Google Chrome");
        source.Set(chrome);
        Assert.Equal(80, mixer.MasterVolumePercent);
        var changed = new List<string?>();
        mixer.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        source.ChangeMasterInWindows(0.35f, muted: true);          // lowered and muted in Windows
        Assert.Contains(nameof(AudioMixerViewModel.MasterVolumePercent), changed);
        Assert.Equal(35, mixer.MasterVolumePercent);
        Assert.True(mixer.MasterMuted);

        mixer.Apps[0].VolumePercent = 20;                           // an app's volume
        mixer.Apps[0].ToggleMuteCommand.Execute(null);
        Assert.Equal(0.35f, source.MasterVolume, 3);                // the Windows volume did not move
        Assert.True(source.MasterMuted);

        mixer.MasterVolumePercent = 60;                             // the Windows volume row itself
        mixer.ToggleMasterMuteCommand.Execute(null);
        Assert.Equal(0.60f, source.MasterVolume, 3);
        Assert.False(source.MasterMuted);
        Assert.Equal(0.20f, chrome.Volume, 3);                      // apps untouched by the master row
    }

    [Fact]
    public void FloatingOpacityIsClampedAndSaved()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        Assert.Equal(1, mixer.FloatingOpacity);

        mixer.FloatingOpacityPercent = 10;                          // below the minimum
        Assert.Equal(AudioMixerViewModel.MinFloatingOpacity, mixer.FloatingOpacity);
        mixer.FloatingOpacityPercent = 65;
        mixer.SaveNow();

        var reloaded = NewMixer(new FakeSource(), freshFolder: false);
        Assert.Equal(0.65, reloaded.FloatingOpacity, 3);
        Assert.Equal(65, reloaded.FloatingOpacityPercent);
    }

    [Fact]
    public void PlayingCountFollowsTheApps()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        var chrome = new FakeApp("chrome.exe", "Google Chrome") { IsPlayingSound = true };
        var spotify = new FakeApp("spotify.exe", "Spotify") { IsPlayingSound = false };
        source.Set(chrome, spotify);
        Assert.Equal(1, mixer.PlayingCount);

        spotify.IsPlayingSound = true;
        spotify.ChangeOutside(1f, false);        // the service reports the new playing state through Changed
        Assert.Equal(2, mixer.PlayingCount);
        Assert.Equal(Loc("Mixer.PlayingNow", 2), mixer.PlayingText);
    }

    [Fact]
    public void ApplyListsSavedAppsThatAreNotRunning()
    {
        var profile = new AudioProfile
        {
            Kind = AudioProfileKind.Streaming,
            Entries = [new() { App = "obs64.exe", Name = "OBS Studio", Volume = 0.5f }, new() { App = "discord.exe", Name = "Discord", Volume = 0.7f }],
        };
        var discord = new FakeApp("discord.exe", "Discord");

        var result = AudioProfiles.Apply(profile, [discord]);

        Assert.Equal(1, result.Applied);
        Assert.Equal(["OBS Studio"], result.NotRunning);
        Assert.Equal(0.7f, discord.Volume, 3);
    }

    [Fact]
    public void EmptyProfileAndEmptyMixChangeNothing()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        var meeting = mixer.Profiles.Single(p => p.Profile.Kind == AudioProfileKind.Meeting);
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.4f);
        source.Set(chrome);

        meeting.ApplyCommand.Execute(null);      // empty profile: nothing applied
        Assert.Equal(0, chrome.VolumeWrites);

        meeting.SaveCommand.Execute(null);
        source.Set();                            // no app has sound now
        meeting.SaveCommand.Execute(null);       // must not wipe the saved mix
        Assert.Single(meeting.Profile.Entries);
    }

    [Fact]
    public void CustomProfilesRoundTripAndBuiltInsStay()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        source.Set(new FakeApp("chrome.exe", "Google Chrome", 0.2f), new FakeApp("discord.exe", "Discord", 0.9f, muted: true));
        mixer.NewProfileName = "  Ranked  ";
        var custom = mixer.CreateProfile();
        Assert.NotNull(custom);
        Assert.Equal("Ranked", custom.Name);
        Assert.Equal(string.Empty, mixer.NewProfileName);
        mixer.ReportFloatingPosition(new System.Windows.Point(1500, 320));
        mixer.SaveNow();

        var reloaded = NewMixer(new FakeSource(), freshFolder: false);
        Assert.Equal([AudioProfileKind.Gaming, AudioProfileKind.Streaming, AudioProfileKind.Meeting, AudioProfileKind.Studio, AudioProfileKind.Custom],
            reloaded.Profiles.Select(p => p.Profile.Kind));
        var ranked = reloaded.Profiles.Last();
        Assert.Equal("Ranked", ranked.Name);
        Assert.Equal(["discord.exe", "chrome.exe"], ranked.Profile.Entries.Select(e => e.App)); // by name: Discord, Google Chrome
        Assert.True(ranked.Profile.Entries[0].Muted);
        Assert.Equal(new System.Windows.Point(1500, 320), reloaded.FloatingPosition);
    }

    [Fact]
    public void OnlyCustomProfilesCanBeDeletedAndDeleteAsksFirst()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        var gaming = mixer.Profiles[0];
        Assert.False(gaming.DeleteCommand.CanExecute(null));

        mixer.NewProfileName = "Night";
        var night = mixer.CreateProfile()!;
        night.DeleteCommand.Execute(null);
        Assert.True(night.IsConfirmingDelete);
        Assert.Contains(night, mixer.Profiles);
        night.ConfirmDeleteCommand.Execute(null);
        Assert.DoesNotContain(night, mixer.Profiles);
        Assert.Equal(4, mixer.Profiles.Count);
    }

    [Fact]
    public void SanitizeCleansAHandEditedFile()
    {
        var loaded = new List<AudioProfile?>
        {
            null,
            new() { Kind = AudioProfileKind.Custom, Name = "   " },                     // no name: dropped
            new()
            {
                Kind = AudioProfileKind.Custom, Name = " Mine ",
                Entries =
                [
                    new() { App = "Chrome.EXE", Name = "", Volume = 7f },
                    new() { App = "chrome.exe", Name = "Chrome", Volume = 0.4f },          // duplicate: last wins
                    new() { App = "", Volume = 0.5f },                                   // no app: dropped
                    new() { App = "x.exe", Volume = float.NaN },
                ],
            },
            new() { Kind = AudioProfileKind.Gaming },
            new() { Kind = AudioProfileKind.Gaming },                                   // duplicate built-in: dropped
        };

        var profiles = AudioProfiles.Sanitize(loaded);

        Assert.Equal(5, profiles.Count);                                                 // 4 built-ins + Mine
        var mine = profiles.Single(p => p.Kind == AudioProfileKind.Custom);
        Assert.Equal("Mine", mine.Name);
        Assert.Equal(["chrome.exe", "x.exe"], mine.Entries.Select(e => e.App));
        Assert.Equal(0.4f, mine.Entries[0].Volume);
        Assert.Equal(1f, mine.Entries[1].Volume);
        Assert.All(profiles, p => Assert.NotEqual(Guid.Empty, p.Id));
    }

    [Fact]
    public void CorruptFileStartsWithTheBuiltInProfiles()
    {
        DataFolderCollection.UseTemporaryFolder();
        File.WriteAllText(Path.Combine(JsonStore.DataDirectory, AudioMixerViewModel.FileName), "{ not json");
        var log = new ActivityLog();

        var mixer = NewMixer(new FakeSource(), log: log, freshFolder: false);

        Assert.Equal(4, mixer.Profiles.Count);
        Assert.True(File.Exists(Path.Combine(JsonStore.DataDirectory, AudioMixerViewModel.FileName + ".corrupt")));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome.exe")]
    [InlineData(@"D:\Games\Discord\Discord.EXE", "discord.exe")]
    [InlineData(null, "")]
    public void ProfileKeyIsTheExecutableFileName(string? path, string key) => Assert.Equal(key, AudioProfiles.KeyFor(path));

    [Fact]
    public void SeveralSessionsAreShownOnTheRow()
    {
        var source = new FakeSource();
        var mixer = NewMixer(source);
        source.Set(new FakeApp("chrome.exe", "Google Chrome") { SessionCount = 3 }, new FakeApp("spotify.exe", "Spotify"));

        Assert.True(mixer.Apps[0].HasSeveralSessions);
        Assert.Equal(Loc("Mixer.Sessions", 3), mixer.Apps[0].SessionsText);
        Assert.False(mixer.Apps[1].HasSeveralSessions);
        Assert.Equal(string.Empty, mixer.Apps[1].SessionsText);
    }

    [Fact]
    public void MixerHotkeyHasADefaultThatDoesNotClash() => RunSta(() =>
    {
        using var hotkeys = new HotkeyService();
        var mixer = hotkeys.Bindings.Single(b => b.Action == HotkeyAction.ToggleMixer);
        Assert.Equal("Ctrl + Shift + F3", mixer.Gesture);
        Assert.Equal(hotkeys.Bindings.Count, hotkeys.Bindings.Select(b => b.Current).Distinct().Count());
        Assert.Equal(hotkeys.Bindings.Count, hotkeys.Bindings.Select(b => b.Id).Distinct().Count());
    });

    /// <summary>The hotkey window is a WPF HwndSource, which needs an STA thread.</summary>
    private static void RunSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static string Loc(string key, params object[] args) => VIZEN.App.Localization.Loc.F(key, args);
}
