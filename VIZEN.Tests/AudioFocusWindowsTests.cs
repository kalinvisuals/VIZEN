using System.Diagnostics;
using System.Windows.Threading;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.Tests;

/// <summary>
/// Smart Audio Focus against real Windows audio (VIZEN_AUDIO_TESTS=1): a "priority" app (Windows PowerShell playing a
/// quiet tone) and an app to lower (a copy of PowerShell under another name, also playing the tone). The real
/// <see cref="AppAudioService"/> drives the engine on its meter ticks; the lowered volume and its return are read by an
/// independent Core Audio reader.
/// </summary>
[Collection(AudioCollection.Name)]
public class AudioFocusWindowsTests
{
    private const string DuckedExe = "vizen-focus-ducked.exe";

    [AudioFact]
    public void OtherAppIsLoweredWhileThePriorityAppPlaysAndComesBack() => Sta(() =>
    {
        var copy = Path.Combine(Path.GetTempPath(), "vizen-focus-test", DuckedExe);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        if (!File.Exists(copy))
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), copy);
        }

        using var ducked = StartTone(copy);
        using var service = new AppAudioService();
        var settings = new AudioFocusSettings
        {
            Enabled = true,
            Priority = new AudioFocusApp { App = "powershell.exe", Name = "Windows PowerShell" },
            AllOthers = false,
            Ducked = [new AudioFocusApp { App = DuckedExe, Name = "Ducked" }],
            DuckPercent = 50,
            Sensitivity = 90,   // the test tone is quiet (3 %)
            AttackMs = 100,
            HoldMs = 300,
            ReleaseMs = 300,
        };
        var engine = new AudioFocusEngine();
        service.MeterTick += (_, ms) => engine.Step(ms, settings, service.MixerApps);
        service.SetFocusActive(true);
        try
        {
            Assert.True(PumpUntil(() => Volume(DuckedExe) is > 0.99f, 6), "the app to lower is not at full volume at the start");
            Pump(1.0);
            Assert.Equal(1f, Volume(DuckedExe) ?? 0, 2);                      // the priority app is silent: nothing changes

            using (StartTone(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")))
            {
                Assert.True(PumpUntil(() => Volume(DuckedExe) is > 0.45f and < 0.55f, 8), $"not lowered to 50 % (now {Volume(DuckedExe)})");
                Assert.Equal(1f, Volume("powershell.exe") ?? 0, 2);           // the priority app itself is never lowered
            }

            Assert.True(PumpUntil(() => Volume(DuckedExe) is > 0.99f, 8), $"did not come back (now {Volume(DuckedExe)})");
            Assert.Empty(engine.Lowered);
        }
        finally
        {
            engine.RestoreAll();
            service.SetFocusActive(false);
        }
    });

    /// <summary>Volume of an app's session read by a separate service (null when the app has no session).</summary>
    private static float? Volume(string exe)
    {
        using var reader = new AppAudioService();
        reader.RefreshSessions();
        return reader.MixerApps.FirstOrDefault(a => a.Key == exe)?.Volume;
    }

    private static bool PumpUntil(Func<bool> condition, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            Pump(0.15);
        }

        return condition();
    }

    /// <summary>Runs the dispatcher (the service's timers) for <paramref name="seconds"/>.</summary>
    private static void Pump(double seconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static Tone StartTone(string exe) => new(exe);

    private sealed class Tone : IDisposable
    {
        private readonly Process _process;

        public Tone(string exe)
        {
            using (AudioTone.Start()) { } // makes sure the tone file exists
            var wav = Path.Combine(Path.GetTempPath(), "vizen-mixer-tone.wav");
            _process = Process.Start(new ProcessStartInfo(exe,
                $"-NoProfile -NonInteractive -Command \"(New-Object Media.SoundPlayer '{wav}').PlayLooping(); Start-Sleep 60\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;
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
