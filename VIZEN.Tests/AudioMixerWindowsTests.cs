using System.Diagnostics;
using NAudio.CoreAudioApi;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.Tests;

/// <summary>Runs only when VIZEN_AUDIO_TESTS=1: these tests play a quiet tone through the real Windows audio stack.</summary>
public sealed class AudioFactAttribute : FactAttribute
{
    public AudioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VIZEN_AUDIO_TESTS") != "1")
        {
            Skip = "Set VIZEN_AUDIO_TESTS=1 to run the tests with real Windows audio (they play a quiet 440 Hz tone).";
        }
    }
}

/// <summary>
/// Smart Audio Mixer against real Windows Core Audio: a separate process plays a quiet tone; the real
/// <see cref="AppAudioService"/> must find it, change only its session volume and mute (checked with an independent
/// reader, master volume unchanged), notice when it closes and when it starts again, and read its level.
/// </summary>
[Collection(AudioCollection.Name)]
public class AudioMixerWindowsTests
{
    private const string ToneApp = "powershell.exe";

    [AudioFact]
    public void FindsAToneAppAndLosesItWhenItCloses() => Sta(() =>
    {
        using var service = new AppAudioService();
        Assert.Null(Find(service));
        using (var tone = AudioTone.Start())
        {
            var app = WaitFor(service, a => a is not null);
            Assert.Equal(ToneApp, app!.Key);
            Assert.Equal("Windows PowerShell", app.AppName);
            Assert.True(app.SessionCount >= 1);
            Assert.NotNull(service.IconFor(app));
        }

        Assert.Null(WaitFor(service, a => a is null));
    });

    [AudioFact]
    public void VolumeAndMuteChangeOnlyTheAppSessionNotTheMasterVolume() => Sta(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var masterBefore = device.AudioEndpointVolume.MasterVolumeLevelScalar;
        var masterMutedBefore = device.AudioEndpointVolume.Mute;

        using var tone = AudioTone.Start();
        using var service = new AppAudioService();
        var app = WaitFor(service, a => a is not null)!;
        try
        {
            app.Volume = 0.25f;
            app.Muted = true;

            using var independent = new AppAudioService();   // a second reader with its own Windows objects
            var seen = WaitFor(independent, a => a is not null)!;
            Assert.Equal(0.25f, seen.Volume, 2);
            Assert.True(seen.Muted);

            Assert.Equal(masterBefore, device.AudioEndpointVolume.MasterVolumeLevelScalar, 3);
            Assert.Equal(masterMutedBefore, device.AudioEndpointVolume.Mute);
        }
        finally
        {
            app.Volume = 1f;
            app.Muted = false;
        }
    });

    [AudioFact]
    public void AppThatRestartsComesBackWithANewSession() => Sta(() =>
    {
        using var service = new AppAudioService();
        using (AudioTone.Start())
        {
            Assert.NotNull(WaitFor(service, a => a is not null));
        }

        Assert.Null(WaitFor(service, a => a is null));
        using (AudioTone.Start())
        {
            var again = WaitFor(service, a => a is not null);
            Assert.NotNull(again);
            Assert.True(again!.SessionCount >= 1);
        }
    });

    [AudioFact]
    public void LevelMeterReadsTheTone() => Sta(() =>
    {
        using var tone = AudioTone.Start();
        using var service = new AppAudioService();
        var app = (AppAudio)WaitFor(service, a => a is not null)!;
        var peak = 0f;
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until && peak <= 0.001f)
        {
            app.UpdatePeak();
            peak = Math.Max(peak, app.Peak);
            Thread.Sleep(70);
        }

        Assert.True(peak > 0.001f, $"peak stayed at {peak}");
    });

    [AudioFact]
    public void ProfileAppliesToTheRealSession() => Sta(() =>
    {
        using var tone = AudioTone.Start();
        using var service = new AppAudioService();
        var app = WaitFor(service, a => a is not null)!;
        try
        {
            var profile = new AudioProfile { Kind = AudioProfileKind.Gaming, Entries = [new() { App = ToneApp, Name = "PowerShell", Volume = 0.4f, Muted = false }] };
            var result = AudioProfiles.Apply(profile, service.MixerApps);
            Assert.Equal(1, result.Applied);
            using var independent = new AppAudioService();
            Assert.Equal(0.4f, WaitFor(independent, a => a is not null)!.Volume, 2);
        }
        finally
        {
            app.Volume = 1f;
        }
    });

    [AudioFact]
    public void WindowsVolumeChangedInWindowsShowsUpInTheMixer() => Sta(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var before = device.AudioEndpointVolume.MasterVolumeLevelScalar;
        var target = before > 0.5f ? before - 0.2f : before + 0.2f;
        using var service = new AppAudioService();
        var changes = 0;
        service.MasterChanged += (_, _) => changes++;
        service.SetMixerActive(true);
        try
        {
            device.AudioEndpointVolume.MasterVolumeLevelScalar = target;   // as if changed in the Windows taskbar
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until && Math.Abs(service.MasterVolume - target) > 0.01f)
            {
                PumpOnce();
            }

            Assert.Equal(target, service.MasterVolume, 2);
            Assert.True(changes > 0);
            Assert.False(string.IsNullOrEmpty(service.MasterDeviceName));
        }
        finally
        {
            device.AudioEndpointVolume.MasterVolumeLevelScalar = before;   // the user's volume back exactly
            service.SetMixerActive(false);
        }
    });

    [AudioFact]
    public void AppVolumeChangedOutsideVizenUpdatesItsRow() => Sta(() =>
    {
        using var tone = AudioTone.Start();
        using var service = new AppAudioService();
        var app = WaitFor(service, a => a is not null)!;
        var changes = 0;
        app.Changed += (_, _) => changes++;
        try
        {
            using (var other = new AppAudioService())    // another program (like the Windows volume mixer) changes it
            {
                var outside = WaitFor(other, a => a is not null)!;
                outside.Volume = 0.3f;
                outside.Muted = true;
            }

            service.RefreshSessions();                    // the next scan (every second while the mixer is visible)
            Assert.True(changes > 0, "no change reported");
            Assert.Equal(0.3f, app.Volume, 2);
            Assert.True(app.Muted);
        }
        finally
        {
            app.Volume = 1f;
            app.Muted = false;
        }
    });

    /// <summary>Runs the dispatcher (the service's meter timer) for ~100 ms.</summary>
    private static void PumpOnce()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [AudioFact]
    public void DeviceNotificationsRegisterAndUnregister() => Sta(() =>
    {
        // Windows accepts the endpoint notification client (device added / removed / default changed) and lets it go.
        var notifications = VIZEN.App.Interop.AudioDeviceNotifications.TryStart(() => { });
        Assert.NotNull(notifications);
        notifications!.Dispose();
        notifications.Dispose();   // twice is harmless
    });

    private static IMixerApp? Find(AppAudioService service)
    {
        service.RefreshSessions();
        return service.MixerApps.FirstOrDefault(a => a.Key == ToneApp);
    }

    /// <summary>Rescans until the condition holds (up to 6 s) and returns the tone app (or null).</summary>
    private static IMixerApp? WaitFor(AppAudioService service, Func<IMixerApp?, bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(6);
        IMixerApp? app;
        do
        {
            app = Find(service);
            if (condition(app))
            {
                return app;
            }

            Thread.Sleep(150);
        }
        while (DateTime.UtcNow < until);

        return app;
    }

    private static void Sta(Action test)
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
}

/// <summary>A Windows PowerShell process looping a quiet 440 Hz tone (amplitude 3 %).</summary>
internal sealed class AudioTone : IDisposable
{
    private readonly Process _process;

    private AudioTone(Process process) => _process = process;

    public static AudioTone Start()
    {
        var wav = Path.Combine(Path.GetTempPath(), "vizen-mixer-tone.wav");
        if (!File.Exists(wav))
        {
            WriteTone(wav);
        }

        var process = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"(New-Object Media.SoundPlayer '{wav}').PlayLooping(); Start-Sleep 60\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        return new AudioTone(process);
    }

    public void Dispose()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }

        _process.Dispose();
    }

    private static void WriteTone(string path)
    {
        const int rate = 44100;
        var samples = rate; // one second, looped
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * short.MaxValue * 0.03));
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioCollection
{
    public const string Name = "Real Windows audio";
}
