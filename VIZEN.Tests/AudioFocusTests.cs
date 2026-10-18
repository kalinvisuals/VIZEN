using VIZEN.App.Models;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;
using FakeApp = VIZEN.Tests.AudioMixerTests.FakeApp;
using FakeSource = VIZEN.Tests.AudioMixerTests.FakeSource;
using FakePresenter = VIZEN.Tests.AudioMixerTests.FakePresenter;

namespace VIZEN.Tests;

/// <summary>Smart Audio Focus with fake apps and a controlled clock (no Windows audio calls).</summary>
[Collection(DataFolderCollection.Name)]
public class AudioFocusTests
{
    private static AudioFocusSettings Rule(int duck = 60, int attack = 200, int hold = 500, int release = 1000, bool allOthers = true) => new()
    {
        Enabled = true,
        Priority = new AudioFocusApp { App = "discord.exe", Name = "Discord" },
        AllOthers = allOthers,
        DuckPercent = duck,
        Sensitivity = 60,
        AttackMs = attack,
        HoldMs = hold,
        ReleaseMs = release,
    };

    private static float Loud => 0.5f;

    private static void Run(AudioFocusEngine engine, AudioFocusSettings rule, IReadOnlyList<IMixerApp> apps, double ms, double step = 50)
    {
        for (var t = 0.0; t < ms - 0.001; t += step)
        {
            engine.Step(step, rule, apps);
        }
    }

    [Fact]
    public void EnvelopeGoesDownHoldsAndComesBackOnTime()
    {
        var ducker = new AudioDucker();
        var rule = Rule(duck: 60, attack: 200, hold: 500, release: 1000);

        ducker.Step(100, Loud, rule);
        Assert.Equal(0.7, ducker.Factor, 3);              // half way down after half the attack
        ducker.Step(100, Loud, rule);
        Assert.Equal(0.4, ducker.Factor, 3);              // full depth (down to 40 %)
        Assert.True(ducker.IsActive);

        ducker.Step(400, 0, rule);                         // quiet, but still inside the hold
        Assert.Equal(0.4, ducker.Factor, 3);
        Assert.False(ducker.IsActive);
        ducker.Step(200, 0, rule);                         // hold over (600 ms quiet): the release has started
        Assert.Equal(0.52, ducker.Factor, 3);             // 0.4 + 0.6 x 200 / 1000
        ducker.Step(1000, 0, rule);
        Assert.Equal(1, ducker.Factor, 3);
        Assert.False(ducker.IsDucking);
    }

    [Fact]
    public void QuietSoundBelowTheThresholdDoesNotDuck()
    {
        var ducker = new AudioDucker();
        var rule = Rule();
        var threshold = AudioFocusSettings.ThresholdFor(rule.Sensitivity);

        ducker.Step(500, threshold * 0.8f, rule);
        Assert.Equal(1, ducker.Factor);
        Assert.True(AudioFocusSettings.ThresholdFor(90) < AudioFocusSettings.ThresholdFor(30)); // more sensitive = lower threshold
    }

    [Fact]
    public void OnlyTheChosenAppsAreLoweredAndGetTheirOwnVolumeBack()
    {
        var discord = new FakeApp("discord.exe", "Discord", 0.9f);
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.8f);
        var spotify = new FakeApp("spotify.exe", "Spotify", 0.5f);
        var system = new FakeApp(AudioProfiles.SystemKey, "System sounds", 1f, system: true);
        IReadOnlyList<IMixerApp> apps = [discord, chrome, spotify, system];
        var rule = Rule(allOthers: false);
        rule.Ducked.Add(new AudioFocusApp { App = "chrome.exe", Name = "Google Chrome" });
        var engine = new AudioFocusEngine();

        discord.LevelNow = Loud;
        Run(engine, rule, apps, 300);
        Assert.Equal(0.8f * 0.4f, chrome.Volume, 3);
        Assert.Equal(0.5f, spotify.Volume);               // not chosen
        Assert.Equal(0.9f, discord.Volume);               // the priority app is never lowered
        Assert.Equal(1f, system.Volume);
        Assert.Equal(0.8f, engine.BaseFor(chrome)!.Value, 3);

        discord.LevelNow = 0;
        Run(engine, rule, apps, 2000);
        Assert.Equal(0.8f, chrome.Volume, 3);
        Assert.Null(engine.BaseFor(chrome));
    }

    [Fact]
    public void AllOthersLeavesThePriorityAppAndSystemSoundsAlone()
    {
        var discord = new FakeApp("discord.exe", "Discord");
        var chrome = new FakeApp("chrome.exe", "Google Chrome");
        var game = new FakeApp("cs2.exe", "Counter-Strike 2");
        var system = new FakeApp(AudioProfiles.SystemKey, "System sounds", system: true);
        IReadOnlyList<IMixerApp> apps = [discord, chrome, game, system];
        var engine = new AudioFocusEngine();

        discord.LevelNow = Loud;
        Run(engine, Rule(duck: 50), apps, 300);

        Assert.Equal(0.5f, chrome.Volume, 3);
        Assert.Equal(0.5f, game.Volume, 3);
        Assert.Equal(1f, discord.Volume);
        Assert.Equal(1f, system.Volume);
    }

    [Fact]
    public void VolumeSetInVizenWhileLoweredBecomesTheAppsOwnVolume()
    {
        var discord = new FakeApp("discord.exe", "Discord");
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.8f);
        IReadOnlyList<IMixerApp> apps = [discord, chrome];
        var rule = Rule(duck: 50);
        var engine = new AudioFocusEngine();
        discord.LevelNow = Loud;
        Run(engine, rule, apps, 300);

        Assert.True(engine.UserSetVolume(chrome, 0.6f));  // the user lowers Chrome while it is ducked
        Assert.Equal(0.3f, chrome.Volume, 3);             // still lowered by half
        discord.LevelNow = 0;
        Run(engine, rule, apps, 2500);
        Assert.Equal(0.6f, chrome.Volume, 3);             // comes back to the user's new volume
        Assert.False(engine.UserSetVolume(chrome, 0.7f)); // not lowered any more: the caller sets it directly
    }

    [Fact]
    public void ChangeByAnotherProgramDuringADuckIsUndoneAndTheOwnVolumeKept()
    {
        var discord = new FakeApp("discord.exe", "Discord");
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.8f);
        IReadOnlyList<IMixerApp> apps = [discord, chrome];
        var rule = Rule(duck: 50);
        var engine = new AudioFocusEngine();
        discord.LevelNow = Loud;
        Run(engine, rule, apps, 300);

        chrome.ChangeOutside(0.05f, false);              // e.g. Discord's own attenuation lowers Chrome as well
        Run(engine, rule, apps, 100);
        Assert.Equal(0.4f, chrome.Volume, 3);             // not lowered twice
        discord.LevelNow = 0;
        Run(engine, rule, apps, 2500);
        Assert.Equal(0.8f, chrome.Volume, 3);             // the user's own volume, not the attenuated one
    }

    [Fact]
    public void AppThatGoesAwayIsForgottenAndRestoreAllPutsEverythingBack()
    {
        var discord = new FakeApp("discord.exe", "Discord");
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.8f);
        var spotify = new FakeApp("spotify.exe", "Spotify", 0.6f);
        var rule = Rule(duck: 50);
        var engine = new AudioFocusEngine();
        discord.LevelNow = Loud;
        Run(engine, rule, [discord, chrome, spotify], 300);
        Assert.Equal(2, engine.Lowered.Count);

        Run(engine, rule, [discord, chrome], 100);        // Spotify closed
        Assert.Single(engine.Lowered);

        engine.RestoreAll();
        Assert.Equal(0.8f, chrome.Volume, 3);
        Assert.Empty(engine.Lowered);
        Assert.False(engine.Ducker.IsDucking);
    }

    [Fact]
    public void TurningItOffRestoresAtOnce()
    {
        var discord = new FakeApp("discord.exe", "Discord");
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.8f);
        IReadOnlyList<IMixerApp> apps = [discord, chrome];
        var rule = Rule(duck: 50);
        var engine = new AudioFocusEngine();
        discord.LevelNow = Loud;
        Run(engine, rule, apps, 300);

        rule.Enabled = false;
        engine.Step(50, rule, apps);
        Assert.Equal(0.8f, chrome.Volume, 3);
        Assert.Empty(engine.Lowered);
    }

    [Fact]
    public void RestoreFileBringsVolumesBackAfterAnUnexpectedExit()
    {
        var discord = new FakeApp("discord.exe", "Discord");
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.8f);
        var engine = new AudioFocusEngine();
        discord.LevelNow = Loud;
        Run(engine, Rule(duck: 50), [discord, chrome], 300);
        var saved = engine.RestoreEntries();
        Assert.Equal("chrome.exe", Assert.Single(saved).App);

        // VIZEN "crashed": Chrome stays at 40 % until the next start reads the file.
        var chromeAfterRestart = new FakeApp("chrome.exe", "Google Chrome", 0.4f);
        var restored = AudioFocusEngine.RestoreFromFile(saved, [discord, chromeAfterRestart]);
        Assert.Equal(1, restored);
        Assert.Equal(0.8f, chromeAfterRestart.Volume, 3);
    }

    // ----- view model -----

    private static (AudioMixerViewModel Mixer, FakeSource Source) NewMixer(params FakeApp[] apps)
    {
        DataFolderCollection.UseTemporaryFolder();
        var source = new FakeSource();
        source.Set(apps);
        return (new AudioMixerViewModel(source, new FakePresenter(), new ActivityLog()), source);
    }

    [Fact]
    public void ChoosingAndTurningOnRunsOnMeterTicksAndRowsShowTheOwnVolume()
    {
        var discord = new FakeApp("discord.exe", "Discord");
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.8f);
        var (mixer, source) = NewMixer(discord, chrome);
        var focus = mixer.Focus;
        Assert.Equal(["Discord", "Google Chrome"], focus.PriorityChoices.Select(c => c.Name));

        focus.PriorityChoices[0].ToggleCommand.Execute(null);   // Discord is the priority app
        Assert.Equal("Discord", focus.PriorityName);
        Assert.DoesNotContain(focus.DuckChoices, c => c.App == "discord.exe");
        focus.DuckPercent = 50;
        focus.AttackMs = 100;
        focus.IsEnabled = true;
        Assert.True(source.FocusCalls[^1]);

        discord.LevelNow = 0.5f;
        for (var i = 0; i < 4; i++) source.Tick(50);
        Assert.Equal(0.4f, chrome.Volume, 3);
        Assert.True(focus.IsDucking);
        var row = mixer.Apps.Single(a => a.App == chrome);
        Assert.True(row.IsFocusLowered);
        Assert.Equal(80, row.VolumePercent);                     // the slider shows the volume it comes back to
        Assert.True(File.Exists(Path.Combine(JsonStore.DataDirectory, AudioFocusViewModel.RestoreFileName)));

        row.VolumePercent = 60;                                  // moved by hand while lowered
        Assert.Equal(0.3f, chrome.Volume, 3);

        focus.RestoreCommand.Execute(null);
        Assert.Equal(0.6f, chrome.Volume, 3);
        Assert.False(row.IsFocusLowered);
        Assert.False(File.Exists(Path.Combine(JsonStore.DataDirectory, AudioFocusViewModel.RestoreFileName)));

        focus.IsEnabled = false;
        Assert.False(source.FocusCalls[^1]);
    }

    [Fact]
    public void SettingsAreClampedAndSaved()
    {
        var (mixer, _) = NewMixer(new FakeApp("discord.exe", "Discord"), new FakeApp("spotify.exe", "Spotify"));
        var focus = mixer.Focus;
        focus.PriorityChoices[0].ToggleCommand.Execute(null);
        focus.AllOthers = false;
        focus.DuckChoices.Single(c => c.App == "spotify.exe").ToggleCommand.Execute(null);
        focus.DuckPercent = 500;                                 // clamped
        focus.HoldMs = 1234;                                     // rounded to 50 ms
        focus.IsEnabled = true;
        mixer.SaveNow();

        var reloaded = new AudioMixerViewModel(new FakeSource(), new FakePresenter(), new ActivityLog());
        var settings = reloaded.Focus.Settings;
        Assert.True(settings.Enabled);
        Assert.Equal("discord.exe", settings.Priority!.App);
        Assert.False(settings.AllOthers);
        Assert.Equal(["spotify.exe"], settings.Ducked.Select(d => d.App));
        Assert.Equal(100, settings.DuckPercent);
        Assert.Equal(1250, settings.HoldMs);
        Assert.Equal("Discord", reloaded.Focus.PriorityName);
        Assert.Contains(reloaded.Focus.PriorityChoices, c => c.App == "discord.exe" && !c.IsRunning && c.IsSelected); // saved, not running
    }

    [Fact]
    public void SpeedChoicesSetTheTimingsAndCustomTimingsAreRecognised()
    {
        var (mixer, _) = NewMixer(new FakeApp("discord.exe", "Discord"));
        var focus = mixer.Focus;
        Assert.True(focus.IsNormal);                      // the defaults are "Normal"

        focus.SetReactionCommand.Execute("0");
        Assert.True(focus.IsQuick);
        Assert.Equal((100, 400, 600), (focus.Settings.AttackMs, focus.Settings.HoldMs, focus.Settings.ReleaseMs));
        focus.SetReactionCommand.Execute("2");
        Assert.True(focus.IsSmooth);
        Assert.Equal(2500, focus.Settings.ReleaseMs);

        focus.HoldMs = 3000;                              // changed under Advanced
        Assert.True(focus.IsCustomReaction);
        Assert.False(focus.IsSmooth);
    }

    [Fact]
    public void StartingWithARestoreFilePutsTheVolumesBack()
    {
        DataFolderCollection.UseTemporaryFolder();
        JsonStore.Save(AudioFocusViewModel.RestoreFileName, new List<AudioFocusRestoreEntry> { new() { App = "chrome.exe", Volume = 0.9f } });
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.36f);   // left lowered by a crash
        var source = new FakeSource();
        source.Set(chrome);

        _ = new AudioMixerViewModel(source, new FakePresenter(), new ActivityLog());

        Assert.Equal(0.9f, chrome.Volume, 3);
        Assert.False(File.Exists(Path.Combine(JsonStore.DataDirectory, AudioFocusViewModel.RestoreFileName)));
    }

    [Fact]
    public void ShutdownRestoresLoweredApps()
    {
        var discord = new FakeApp("discord.exe", "Discord");
        var chrome = new FakeApp("chrome.exe", "Google Chrome", 0.8f);
        var (mixer, source) = NewMixer(discord, chrome);
        mixer.Focus.PriorityChoices[0].ToggleCommand.Execute(null);
        mixer.Focus.IsEnabled = true;
        discord.LevelNow = 0.5f;
        for (var i = 0; i < 10; i++) source.Tick(50);
        Assert.True(chrome.Volume < 0.8f);

        mixer.Shutdown();

        Assert.Equal(0.8f, chrome.Volume, 3);
    }
}
