using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.Tests;

[Collection(DataFolderCollection.Name)]
public class SchedulesViewModelTests
{
    private sealed class FakePresenter : IMediaPresenter
    {
        public Task<bool> ShowAsync(MediaLayerViewModel layer, int index) => Task.FromResult(true);
        public void Hide(MediaLayerViewModel layer) { }
        public void Close(MediaLayerViewModel layer) { }
        public void ApplyZOrder(IEnumerable<MediaLayerViewModel> backToFront) { }
    }

    private sealed class NoPicker : IMediaFilePicker
    {
        public IReadOnlyList<string> PickFiles() => [];
    }

    /// <summary>HotkeyService needs a window, so these tests run on an STA thread.</summary>
    private static void OnSta(Action test)
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

    [Fact]
    public void SponsorPlaylistFromGroupRotatesLayersOnTheClock() => OnSta(() =>
    {
        DataFolderCollection.UseTemporaryFolder();
        var library = new MediaLibraryViewModel(new FakePresenter(), new NoPicker(), new ActivityLog());
        var layers = library.ImportFiles([@"C:\s\alpha.png", @"C:\s\beta.png", @"C:\s\other.png"]);
        layers[0].Group = "Sponsors";
        layers[1].Group = "Sponsors";

        var now = new DateTime(2026, 10, 9, 12, 0, 0);
        using var hotkeys = new HotkeyService();
        var page = new SchedulesViewModel(library, hotkeys, new ActivityLog(), () => now);
        var playlist = page.NewFromGroup("Sponsors")!;

        Assert.Equal(2, playlist.Entries.Count);
        Assert.Single(playlist.AvailableLayers); // "other" can still be added
        Assert.Equal(5, playlist.PreviewLines.Count);

        page.Start(playlist);
        var first = library.Items.Single(l => l.IsShown);

        now = now.AddSeconds(10);
        page.Tick();
        Assert.DoesNotContain(library.Items, l => l.IsShown);

        now = now.AddMinutes(5);
        page.Tick();
        var second = library.Items.Single(l => l.IsShown);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Contains("Sponsors", second.Group);

        page.SaveNow();
        var reloaded = new SchedulesViewModel(library, hotkeys, new ActivityLog(), () => now);
        Assert.Equal(2, reloaded.Items.Single().Entries.Count);
    });

    [Fact]
    public void TriggerShowsLayerAndInvalidValuesAreIgnored() => OnSta(() =>
    {
        DataFolderCollection.UseTemporaryFolder();
        var library = new MediaLibraryViewModel(new FakePresenter(), new NoPicker(), new ActivityLog());
        var logo = library.ImportFiles([@"C:\s\logo.png"])[0];
        var now = new DateTime(2026, 10, 9, 12, 0, 0);
        using var hotkeys = new HotkeyService();
        var page = new SchedulesViewModel(library, hotkeys, new ActivityLog(), () => now);
        page.NewScheduleCommand.Execute(null);
        var schedule = page.Items.Single();
        schedule.AddLayerCommand.Execute(logo);

        schedule.VisibleSecondsText = "abc";   // ignored
        schedule.IntervalMinutesText = "2,5"; // comma accepted
        schedule.ActiveFrom = "25:99";        // ignored
        Assert.Equal(10, schedule.Schedule.VisibleSeconds);
        Assert.Equal(150, schedule.Schedule.IntervalSeconds);
        Assert.Null(schedule.Schedule.ActiveFrom);

        page.Trigger(schedule);
        Assert.True(logo.IsShown);
        now = now.AddSeconds(10);
        page.Tick();
        Assert.False(logo.IsShown);
    });
}
