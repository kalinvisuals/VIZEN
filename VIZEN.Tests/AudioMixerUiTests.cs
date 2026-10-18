using System.Diagnostics;
using System.Windows.Automation;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.Tests;

/// <summary>
/// End to end with real Windows audio and the real VIZEN window (VIZEN_AUDIO_TESTS=1): a quiet tone plays in a separate
/// process; VIZEN (Debug build, isolated data folder, English) is driven only through UI Automation patterns (no mouse
/// or keyboard input), and every change is checked with an independent Core Audio reader.
/// </summary>
[Collection(AudioCollection.Name)]
public class AudioMixerUiTests
{
    private const string Row = "Windows PowerShell";

    [AudioFact]
    public void MixerPageAndFloatingMixerControlTheRealSession() => Sta(() =>
    {
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\VIZEN.App\bin\Debug\net10.0-windows10.0.22621.0\VIZEN.App.exe"));
        Assert.True(File.Exists(exe), exe);
        var data = Path.Combine(Path.GetTempPath(), "vizen-mixer-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"language\":\"en\"}");

        var tone = AudioTone.Start();
        Process? vizen = null;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.Environment["VIZEN_DATA_DIR"] = data;
            vizen = Process.Start(start)!;
            var main = Wait(() => TopWindows(vizen.Id).FirstOrDefault(w => w.Current.Name == "VIZEN"), 20);
            Assert.NotNull(main);

            // Navigate to Smart Audio Mixer (sidebar item 9).
            var items = main!.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            ((SelectionItemPattern)items[9].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

            // The tone app appears with its own slider; set it to 40 %.
            var slider = Wait(() => Find(main, ControlType.Slider, "Volume of " + Row), 8);
            Assert.NotNull(slider);
            ((RangeValuePattern)slider!.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(40);
            Assert.Equal(0.40f, ReadTone().Volume, 2);

            // Mute and unmute.
            var mute = Find(main, ControlType.Button, "Mute " + Row)!;
            Invoke(mute);
            Assert.True(ReadTone().Muted);
            Invoke(mute);
            Assert.False(ReadTone().Muted);

            // Gaming profile (segmented control): select it, save 40 %, change to 20 %, click Gaming again -> 40 %.
            Invoke(FindAll(main, ControlType.Button, "Gaming")[0]);
            Invoke(FindAll(main, ControlType.Button, "Save current mix")[0]);
            ((RangeValuePattern)slider.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(20);
            Assert.Equal(0.20f, ReadTone().Volume, 2);
            Invoke(FindAll(main, ControlType.Button, "Gaming")[0]);
            Assert.Equal(0.40f, ReadTone().Volume, 2);

            // Floating mixer: its own slider controls the same session.
            Invoke(Find(main, ControlType.Button, "Show floating mixer")!);
            var floating = Wait(() => TopWindows(vizen.Id).FirstOrDefault(w => w.Current.Name == "VIZEN Smart Audio Mixer"), 5);
            Assert.NotNull(floating);
            var floatingSlider = Wait(() => Find(floating!, ControlType.Slider, "Volume of " + Row), 5)!;
            ((RangeValuePattern)floatingSlider.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(70);
            Assert.Equal(0.70f, ReadTone().Volume, 2);

            // The app closes: its row goes away (page and floating); it starts again: it comes back.
            tone.Dispose();
            Assert.True(WaitTrue(() => Find(main, ControlType.Slider, "Volume of " + Row) is null, 6), "row still there after the app closed");
            tone = AudioTone.Start();
            Assert.NotNull(Wait(() => Find(main, ControlType.Slider, "Volume of " + Row), 8));

            // Hide the floating mixer from its own button.
            Invoke(Find(floating!, ControlType.Button, "Hide the mixer")!);
            Assert.True(WaitTrue(() => !TopWindows(vizen.Id).Any(w => w.Current.Name == "VIZEN Smart Audio Mixer"), 3), "floating mixer still visible");
        }
        finally
        {
            RestoreTone();
            tone.Dispose();
            if (vizen is { HasExited: false })
            {
                vizen.Kill();
                vizen.WaitForExit(5000);
            }
        }

        var saved = File.ReadAllText(Path.Combine(data, "audio.json"));
        Assert.Contains("powershell.exe", saved);
    });

    /// <summary>Volume and mute of the tone app, read by a separate service with its own Windows objects.</summary>
    private static (float Volume, bool Muted) ReadTone()
    {
        Thread.Sleep(150);
        using var service = new AppAudioService();
        service.RefreshSessions();
        var app = service.MixerApps.First(a => a.Key == "powershell.exe");
        return (app.Volume, app.Muted);
    }

    /// <summary>Windows remembers per-app volumes: leave PowerShell at full volume, unmuted.</summary>
    private static void RestoreTone()
    {
        using var service = new AppAudioService();
        service.RefreshSessions();
        foreach (var app in service.MixerApps.Where(a => a.Key == "powershell.exe"))
        {
            app.Volume = 1f;
            app.Muted = false;
        }
    }

    private static IEnumerable<AutomationElement> TopWindows(int pid) =>
        AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid)).Cast<AutomationElement>();

    private static AutomationElement? Find(AutomationElement root, ControlType type, string name) =>
        FindAll(root, type, name).FirstOrDefault(e => !e.Current.IsOffscreen) ?? FindAll(root, type, name).FirstOrDefault();

    private static List<AutomationElement> FindAll(AutomationElement root, ControlType type, string name) =>
        root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, type),
            new PropertyCondition(AutomationElement.NameProperty, name))).Cast<AutomationElement>().ToList();

    private static void Invoke(AutomationElement button)
    {
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(250);
    }

    private static T? Wait<T>(Func<T?> get, int seconds) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            if (get() is { } value)
            {
                return value;
            }

            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < until);

        return null;
    }

    private static bool WaitTrue(Func<bool> condition, int seconds) => Wait(() => condition() ? "ok" : null, seconds) is not null;

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
