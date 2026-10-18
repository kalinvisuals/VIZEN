using VIZEN.App.Media;
using VIZEN.App.Models;

namespace VIZEN.Tests;

public class ScheduleRunnerTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0);
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    private static MediaSchedule Schedule(ScheduleMode mode, params Guid[] layers) => new()
    {
        Mode = mode,
        Entries = layers.Select(l => new ScheduleEntry { LayerId = l }).ToList(),
    };

    private static ScheduleRunner Runner(MediaSchedule s, TimeSpan? length = null, int seed = 7) =>
        new(s, _ => length, new Random(seed));

    private static string Text(IEnumerable<LayerChange> changes) =>
        string.Join(" ", changes.Select(c => (c.Show ? "+" : "-") + (c.LayerId == A ? "A" : c.LayerId == B ? "B" : "C")));

    [Fact]
    public void LogoTenSecondsEveryFiveMinutes()
    {
        var s = Schedule(ScheduleMode.Repeat, A);
        s.VisibleSeconds = 10;
        s.IntervalSeconds = 300;
        var r = Runner(s);

        Assert.Equal("+A", Text(r.Start(T0)));
        Assert.Equal("", Text(r.Tick(T0.AddSeconds(9))));
        Assert.Equal("-A", Text(r.Tick(T0.AddSeconds(10))));
        Assert.Equal(T0.AddMinutes(5), r.NextChangeAt);
        Assert.Equal("", Text(r.Tick(T0.AddSeconds(299))));
        Assert.Equal("+A", Text(r.Tick(T0.AddMinutes(5))));
    }

    [Fact]
    public void PermanentLogoNeverHides()
    {
        var r = Runner(Schedule(ScheduleMode.Permanent, A));
        Assert.Equal("+A", Text(r.Start(T0)));
        Assert.Null(r.NextChangeAt);
        Assert.Equal("", Text(r.Tick(T0.AddHours(5))));
        Assert.Equal(RunnerState.Showing, r.State);
    }

    [Fact]
    public void StartDelayPostponesFirstAppearance()
    {
        var s = Schedule(ScheduleMode.Repeat, A);
        s.StartDelaySeconds = 30;
        var r = Runner(s);
        Assert.Equal("", Text(r.Start(T0)));
        Assert.Equal("+A", Text(r.Tick(T0.AddSeconds(30))));
    }

    [Fact]
    public void MediaLengthDurationUsesGifLoopOrVideoEnd()
    {
        var gif = Schedule(ScheduleMode.Repeat, A);
        gif.VisibleSeconds = 0;
        gif.IntervalSeconds = 600;
        var r = Runner(gif, TimeSpan.FromSeconds(5));
        r.Start(T0);
        Assert.Equal(T0.AddSeconds(5), r.NextChangeAt);

        // Video every 30 min "until it ends": the end event hides it even before the estimated length.
        var video = Schedule(ScheduleMode.Repeat, B);
        video.VisibleSeconds = 0;
        video.IntervalSeconds = 1800;
        var v = Runner(video, TimeSpan.FromSeconds(45));
        v.Start(T0);
        Assert.Equal("-B", Text(v.OnMediaEnded(B, T0.AddSeconds(44))));
        Assert.Equal(T0.AddMinutes(30), v.NextChangeAt);
    }

    [Fact]
    public void MediaEndIsIgnoredWhenAFixedDurationIsSet()
    {
        var s = Schedule(ScheduleMode.Repeat, A);
        s.VisibleSeconds = 20;
        var r = Runner(s);
        r.Start(T0);
        Assert.Empty(r.OnMediaEnded(A, T0.AddSeconds(5)));
        Assert.Equal(RunnerState.Showing, r.State);
    }

    [Fact]
    public void PermanentPlaylistRotatesWithPerItemDurations()
    {
        var s = Schedule(ScheduleMode.Permanent, A, B);
        s.VisibleSeconds = 5;
        s.Entries[1].Seconds = 8;
        var r = Runner(s);

        Assert.Equal("+A", Text(r.Start(T0)));
        Assert.Equal("-A +B", Text(r.Tick(T0.AddSeconds(5))));
        Assert.Equal("", Text(r.Tick(T0.AddSeconds(12))));
        Assert.Equal("-B +A", Text(r.Tick(T0.AddSeconds(13))));
    }

    [Fact]
    public void RandomOrderShowsEveryLayerEachRoundWithoutImmediateRepeats()
    {
        var s = Schedule(ScheduleMode.Permanent, A, B, C);
        s.Order = ScheduleOrder.Random;
        s.VisibleSeconds = 1;
        var shown = new List<Guid>();
        var r = Runner(s, seed: 3);
        var t = T0;
        foreach (var change in r.Start(t).Where(c => c.Show)) shown.Add(change.LayerId);
        for (var i = 0; i < 29; i++)
        {
            t = t.AddSeconds(1);
            shown.AddRange(r.Tick(t).Where(c => c.Show).Select(c => c.LayerId));
        }

        Assert.Equal(30, shown.Count);
        for (var round = 0; round < 10; round++)
        {
            Assert.Equal(3, shown.Skip(round * 3).Take(3).Distinct().Count());
        }

        Assert.DoesNotContain(shown.Zip(shown.Skip(1)), pair => pair.First == pair.Second);
    }

    [Fact]
    public void OnceShowsEachLayerThenFinishes()
    {
        var s = Schedule(ScheduleMode.Once, A, B);
        s.VisibleSeconds = 10;
        var r = Runner(s);
        Assert.Equal("+A", Text(r.Start(T0)));
        Assert.Equal("-A +B", Text(r.Tick(T0.AddSeconds(10))));
        Assert.Equal("-B", Text(r.Tick(T0.AddSeconds(20))));
        Assert.Equal(RunnerState.Finished, r.State);
        Assert.Empty(r.Tick(T0.AddHours(1)));
    }

    [Fact]
    public void DailyWindowHidesOutsideAndWakesUpAtStart()
    {
        var s = Schedule(ScheduleMode.Permanent, A);
        s.ActiveFrom = "20:00";
        s.ActiveUntil = "22:00";
        var r = Runner(s);
        var evening = new DateTime(2026, 10, 9, 19, 0, 0);

        Assert.Empty(r.Start(evening));
        Assert.Equal(new DateTime(2026, 10, 9, 20, 0, 0), r.NextWakeUp(evening));
        Assert.Equal("+A", Text(r.Tick(new DateTime(2026, 10, 9, 20, 0, 0))));
        Assert.Equal("-A", Text(r.Tick(new DateTime(2026, 10, 9, 22, 0, 0))));
        Assert.Equal(new DateTime(2026, 10, 10, 20, 0, 0), r.NextWakeUp(new DateTime(2026, 10, 9, 22, 0, 0)));
    }

    [Fact]
    public void OvernightWindow()
    {
        var s = Schedule(ScheduleMode.Permanent, A);
        s.ActiveFrom = "22:00";
        s.ActiveUntil = "02:00";
        var r = Runner(s);
        Assert.True(r.IsInWindow(new DateTime(2026, 10, 9, 23, 30, 0)));
        Assert.True(r.IsInWindow(new DateTime(2026, 10, 10, 1, 0, 0)));
        Assert.False(r.IsInWindow(new DateTime(2026, 10, 10, 12, 0, 0)));
    }

    [Fact]
    public void ManualTriggerOnStoppedScheduleShowsOnceThenStops()
    {
        var s = Schedule(ScheduleMode.Repeat, A, B);
        s.VisibleSeconds = 10;
        var r = Runner(s);
        Assert.Equal("+A", Text(r.Trigger(T0)));
        Assert.Equal("-A", Text(r.Tick(T0.AddSeconds(10))));
        Assert.Equal(RunnerState.Stopped, r.State);
    }

    [Fact]
    public void ManualTriggerWhileRunningSkipsToNextLayer()
    {
        var s = Schedule(ScheduleMode.Repeat, A, B);
        s.VisibleSeconds = 10;
        s.IntervalSeconds = 60;
        var r = Runner(s);
        r.Start(T0);
        Assert.Equal("-A +B", Text(r.Trigger(T0.AddSeconds(3))));
        Assert.Equal("-B", Text(r.Tick(T0.AddSeconds(13))));
        Assert.Equal(T0.AddSeconds(63), r.NextChangeAt);
    }

    [Fact]
    public void PauseHidesAndResumeContinues()
    {
        var r = Runner(Schedule(ScheduleMode.Permanent, A));
        r.Start(T0);
        Assert.Equal("-A", Text(r.Pause()));
        Assert.Empty(r.Tick(T0.AddMinutes(1)));
        Assert.Equal("+A", Text(r.Resume(T0.AddMinutes(2))));
        Assert.Equal("-A", Text(r.Stop()));
        Assert.Equal(RunnerState.Stopped, r.State);
    }

    [Fact]
    public void PreviewListsUpcomingAppearances()
    {
        var s = Schedule(ScheduleMode.Repeat, A, B);
        s.VisibleSeconds = 10;
        s.IntervalSeconds = 300;
        var plan = ScheduleRunner.Preview(s, _ => null, T0, 3);
        Assert.Equal([T0, T0.AddMinutes(5), T0.AddMinutes(10)], plan.Select(p => p.At));
        Assert.Equal([A, B, A], plan.Select(p => p.LayerId));
        Assert.All(plan, p => Assert.Equal(TimeSpan.FromSeconds(10), p.Duration));
    }

    [Fact]
    public void IndependentSchedulesRunSideBySide()
    {
        // The product example: logo 10 s every 5 min, a permanent logo, a GIF every 10 min, a video every 30 min.
        var sponsor = Schedule(ScheduleMode.Repeat, A); sponsor.VisibleSeconds = 10; sponsor.IntervalSeconds = 300;
        var permanent = Schedule(ScheduleMode.Permanent, B);
        var gif = Schedule(ScheduleMode.Repeat, C); gif.VisibleSeconds = 0; gif.IntervalSeconds = 600;
        var runners = new[] { Runner(sponsor), Runner(permanent), Runner(gif, TimeSpan.FromSeconds(4)) };

        var visible = new HashSet<Guid>();
        void Apply(IEnumerable<LayerChange> changes) { foreach (var c in changes) { if (c.Show) visible.Add(c.LayerId); else visible.Remove(c.LayerId); } }
        foreach (var r in runners) Apply(r.Start(T0));
        Assert.Equal(3, visible.Count);

        foreach (var r in runners) Apply(r.Tick(T0.AddSeconds(5)));   // GIF ended after one loop
        Assert.Equal(new HashSet<Guid> { A, B }, visible);
        foreach (var r in runners) Apply(r.Tick(T0.AddSeconds(11)));  // sponsor's 10 s are over
        Assert.Equal(new HashSet<Guid> { B }, visible);
        foreach (var r in runners) Apply(r.Tick(T0.AddMinutes(5)));   // sponsor again, GIF not yet
        Assert.Equal(new HashSet<Guid> { A, B }, visible);
        foreach (var r in runners) Apply(r.Tick(T0.AddMinutes(10)));  // both repeat at 10 min
        Assert.Equal(new HashSet<Guid> { A, B, C }, visible);
    }

    [Theory]
    [InlineData("20:00", 20, 0)]
    [InlineData("7:05", 7, 5)]
    [InlineData("25:00", -1, -1)]
    [InlineData("abc", -1, -1)]
    public void ParsesTimes(string text, int hours, int minutes)
    {
        var parsed = ScheduleRunner.ParseTime(text);
        if (hours < 0)
        {
            Assert.Null(parsed);
        }
        else
        {
            Assert.Equal(new TimeSpan(hours, minutes, 0), parsed);
        }
    }
}

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl + Shift + F9")]
    [InlineData("Ctrl + Alt + 1")]
    [InlineData("Shift + Win + Num5")]
    public void RoundTrips(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(text, gesture.ToString());
    }

    [Theory]
    [InlineData("F9")]          // no modifier
    [InlineData("Ctrl + Shift")] // no key
    [InlineData("Ctrl + Foo")]
    [InlineData("")]
    public void RejectsInvalid(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void MapsToWin32()
    {
        HotkeyGesture.TryParse("ctrl+shift+f10", out var g);
        Assert.Equal(0x0006u, g.Win32Modifiers);
        Assert.Equal(0x79u, g.VirtualKey);
    }
}
