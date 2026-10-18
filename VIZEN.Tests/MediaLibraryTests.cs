using System.Text.Json;
using System.Windows;
using VIZEN.App.Models;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.Tests;

/// <summary>Media library and layer logic, with fake windows and a temporary data folder.</summary>
[Collection(DataFolderCollection.Name)]
public class MediaLibraryTests
{
    private sealed class FakePresenter : IMediaPresenter
    {
        public List<string> Calls { get; } = [];
        public Task<bool> ShowAsync(MediaLayerViewModel layer, int index) { Calls.Add("show " + layer.Name); return Task.FromResult(true); }
        public void Hide(MediaLayerViewModel layer) => Calls.Add("hide " + layer.Name);
        public void Close(MediaLayerViewModel layer) => Calls.Add("close " + layer.Name);
        public void ApplyZOrder(IEnumerable<MediaLayerViewModel> backToFront) => Calls.Add("z " + string.Join(",", backToFront.Select(l => l.Name)));
    }

    private sealed class NoPicker : IMediaFilePicker
    {
        public IReadOnlyList<string> PickFiles() => [];
    }

    private static MediaLibraryViewModel NewLibrary(FakePresenter presenter, ActivityLog? log = null)
    {
        DataFolderCollection.UseTemporaryFolder();
        return new MediaLibraryViewModel(presenter, new NoPicker(), log ?? new ActivityLog());
    }

    [Fact]
    public void ImportAddsSupportedFilesInFrontAndRejectsOthers()
    {
        var log = new ActivityLog();
        var library = NewLibrary(new FakePresenter(), log);

        library.ImportFiles([@"C:\media\logo.png", @"C:\media\readme.txt"]);
        library.ImportFiles([@"C:\media\promo.mp4"]);

        Assert.Equal(["promo", "logo"], library.Items.Select(i => i.Name));
        Assert.Equal([2, 1], library.Items.Select(i => i.Layer));
        Assert.Equal(MediaKind.Video, library.Items[0].Kind);
        // Without the app's resources, Loc returns the key: the rejection was reported.
        Assert.Contains(log.Entries, e => e.Title == "Media.ImportRejected");
    }

    [Fact]
    public void MovingLayersRenumbersAndRestacksWindows()
    {
        var presenter = new FakePresenter();
        var library = NewLibrary(presenter);
        library.ImportFiles([@"C:\m\a.png", @"C:\m\b.png", @"C:\m\c.png"]); // order: c, b, a

        library.Items[2].BringForwardCommand!.Execute(null); // a moves in front of b

        Assert.Equal(["c", "a", "b"], library.Items.Select(i => i.Name));
        Assert.Equal([3, 2, 1], library.Items.Select(i => i.Layer));
        Assert.Equal("z b,a,c", presenter.Calls.Last()); // back to front
    }

    [Fact]
    public void DuplicateKeepsSettingsWithNewIdentity()
    {
        var library = NewLibrary(new FakePresenter());
        var original = library.ImportFiles([@"C:\m\logo.png"])[0];
        original.Opacity = 0.5;
        original.Group = "Sponsors";

        original.DuplicateCommand!.Execute(null);

        var copy = library.Items.First(i => i.Id != original.Id);
        Assert.Equal(0.5, copy.Opacity);
        Assert.Equal("Sponsors", copy.Group);
        Assert.Equal(["Sponsors"], library.Groups);
    }

    [Fact]
    public void RemoveClosesTheWindowAndSaveRoundTrips()
    {
        var presenter = new FakePresenter();
        var library = NewLibrary(presenter);
        var layers = library.ImportFiles([@"C:\m\one.png", @"C:\m\two.gif"]);
        layers[1].ConfirmRemoveCommand!.Execute(null);
        library.SaveNow();

        Assert.Contains("close two", presenter.Calls);
        var reloaded = new MediaLibraryViewModel(new FakePresenter(), new NoPicker(), new ActivityLog());
        Assert.Equal(["one"], reloaded.Items.Select(i => i.Name));
    }

    [Fact]
    public void GroupActionsAffectOnlyMembers()
    {
        var presenter = new FakePresenter();
        var library = NewLibrary(presenter);
        var layers = library.ImportFiles([@"C:\m\a.png", @"C:\m\b.png"]);
        layers[0].Group = "Sponsors";

        library.LockGroupCommand.Execute("sponsors");

        Assert.True(layers[0].Locked);
        Assert.False(layers[1].Locked);
    }

    [Fact]
    public void CropEdgesAndRotatedAspect()
    {
        var layer = new MediaLayerViewModel(new MediaItem { Name = "x", FilePath = @"C:\x.png" }, () => { });
        layer.CropLeft = 10;
        layer.CropRight = 30;
        Assert.Equal(0.1, layer.Crop.X, 6);
        Assert.Equal(0.6, layer.Crop.Width, 6);
        Assert.Equal(1.0, layer.Crop.Height, 6);

        layer.ReportOpened(1000, 300, null);
        Assert.Equal(2.0, layer.ContentAspect!.Value, 6); // 600 x 300
        layer.Rotation = 90;
        Assert.Equal(0.5, layer.ContentAspect!.Value, 6); // turned on its side
    }

    [Fact]
    public void OpacityIsClampedAndSaved()
    {
        var saves = 0;
        var layer = new MediaLayerViewModel(new MediaItem { Name = "x", FilePath = @"C:\x.png" }, () => saves++);
        layer.Opacity = 2;
        Assert.Equal(1, layer.Opacity);
        layer.Opacity = 0;
        Assert.Equal(0.05, layer.Opacity);
        Assert.Equal(1, saves); // 1 → 1 is no change, 1 → 0.05 is one save
    }
}

/// <summary>Tests that touch JsonStore run one at a time against a throwaway folder.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DataFolderCollection
{
    public const string Name = "Data folder";

    public static void UseTemporaryFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vizen-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        JsonStore.DataDirectory = folder;
    }
}
