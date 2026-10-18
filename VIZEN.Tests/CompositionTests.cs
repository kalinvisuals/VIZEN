using System.Windows;
using VIZEN.App.Capture;
using VIZEN.App.Models;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.Tests;

[Collection(DataFolderCollection.Name)]
public class CompositionTests
{
    private sealed class FakeMedia : IMediaPresenter
    {
        public Task<bool> ShowAsync(MediaLayerViewModel layer, int index) => Task.FromResult(true);
        public void Hide(MediaLayerViewModel layer) { }
        public void Close(MediaLayerViewModel layer) { }
        public void ApplyZOrder(IEnumerable<MediaLayerViewModel> backToFront) { }
    }

    private sealed class FakeWindows : ICompositionPresenter
    {
        public int Open { get; private set; }
        public void Show(CompositionViewModel composition) => Open++;
        public void Close(CompositionViewModel composition) => Open--;
    }

    private sealed class NoPicker : IMediaFilePicker
    {
        public IReadOnlyList<string> PickFiles() => [];
    }

    private static (CompositionsViewModel Page, MediaLibraryViewModel Library, FakeWindows Windows) Setup()
    {
        DataFolderCollection.UseTemporaryFolder();
        var log = new ActivityLog();
        var library = new MediaLibraryViewModel(new FakeMedia(), new NoPicker(), log);
        var capture = new CaptureService();
        var outputs = new OutputsViewModel(capture, new NoOutputs(), log);
        var windows = new FakeWindows();
        var page = new CompositionsViewModel(capture, outputs, library, () => [], windows, log);
        return (page, library, windows);
    }

    private sealed class NoOutputs : IOutputPresenter
    {
        public void Show(OutputViewModel output, int index) { }
    }

    [Fact]
    public void MediaItemsStackFrontFirstAndPersist()
    {
        var (page, library, _) = Setup();
        var logos = library.ImportFiles([@"C:\c\a.png", @"C:\c\b.png"]);
        page.NewCommand.Execute(null);
        var scene = page.Items.Single();

        scene.AddMediaCommand.Execute(logos[0]);
        scene.AddMediaCommand.Execute(logos[1]);
        Assert.Equal(["b", "a"], scene.Items.Select(i => i.Name));
        Assert.Equal([2, 1], scene.Items.Select(i => i.ZIndex));

        scene.Items[1].MoveForwardCommand.Execute(null);
        Assert.Equal(["a", "b"], scene.Items.Select(i => i.Name));
        Assert.Equal([2, 1], scene.Items.Select(i => i.ZIndex));

        page.SaveNow();
        var reloaded = new CompositionsViewModel(new CaptureService(), new OutputsViewModel(new CaptureService(), new NoOutputs(), new ActivityLog()),
            library, () => [], new FakeWindows(), new ActivityLog());
        Assert.Equal(2, reloaded.Items.Single().Items.Count);
    }

    [Fact]
    public void MoveAndResizeUseCanvasPixels()
    {
        var (page, library, _) = Setup();
        var logo = library.ImportFiles([@"C:\c\logo.png"])[0];
        page.NewCommand.Execute(null);
        var scene = page.Items.Single();
        scene.AddMediaCommand.Execute(logo);
        var item = scene.Items.Single();

        item.SetBounds(new Rect(0.1, 0.1, 0.25, 0.25));
        item.MoveBy(192, 108);          // 10% of 1920 x 1080
        item.ResizeBy(-96, 0);          // -5% width
        Assert.Equal(0.2, item.Item.X, 6);
        Assert.Equal(0.2, item.Item.Y, 6);
        Assert.Equal(0.2, item.Item.Width, 6);
        Assert.Equal(384, item.PixelX, 3);

        scene.CanvasPresetCommand.Execute("720p");
        Assert.Equal(256, item.PixelX, 3);     // same relative position on the new canvas
        item.FillCanvasCommand.Execute(null);
        Assert.Equal(1280, item.PixelWidth, 3);
    }

    [Fact]
    public void FollowingItemsAppearOnlyWhileTheLayerIsShown()
    {
        var (page, library, _) = Setup();
        var sponsor = library.ImportFiles([@"C:\c\sponsor.png"])[0];
        page.NewCommand.Execute(null);
        var scene = page.Items.Single();
        scene.AddMediaCommand.Execute(sponsor);
        var item = scene.Items.Single();

        Assert.True(item.IsContentVisible);       // added as always visible
        item.FollowLayerVisibility = true;
        Assert.False(item.IsContentVisible);
        library.SetShownQuietly(sponsor, true);    // e.g. a schedule shows the sponsor
        Assert.True(item.IsContentVisible);
        library.SetShownQuietly(sponsor, false);
        Assert.False(item.IsContentVisible);
    }

    [Fact]
    public void OpenWithMissingSourceReportsItAndCloseReleases()
    {
        var (page, _, windows) = Setup();
        page.NewCommand.Execute(null);
        var scene = page.Items.Single();
        var source = new WindowSource
        {
            Handle = 42, ProcessName = "zoom", ProcessId = 1, ExecutablePath = @"C:\zoom.exe", ClassName = "x", Title = "Zoom Meeting",
            Width = 1600, Height = 900,
        };
        scene.AddCapture(source, new Rect(0, 0, 0.5, 1), "Participant 1");

        page.Open(scene);
        Assert.True(scene.IsOpen);
        Assert.Equal(1, windows.Open);
        Assert.NotNull(scene.Items.Single().MissingText); // no such window right now
        Assert.Null(scene.Items.Single().Capture);

        page.Close(scene);
        Assert.False(scene.IsOpen);
        Assert.Equal(0, windows.Open);
    }
}

