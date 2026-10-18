using System.Windows;
using VIZEN.App.Models;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.Tests;

/// <summary>Smart Draw: geometry, document history, modes and persistence, with fake windows and monitors.</summary>
[Collection(DataFolderCollection.Name)]
public class SmartDrawTests
{
    private sealed class FakePresenter : ISmartDrawPresenter
    {
        public bool IsOpen { get; private set; }
        public int Opened { get; private set; }
        public int Closed { get; private set; }
        public void Open(SmartDrawViewModel drawing) { IsOpen = true; Opened++; }
        public void Close() { IsOpen = false; Closed++; }
    }

    private sealed class FakeMonitors(params MonitorInfo[] monitors) : IMonitorProvider
    {
        public MonitorInfo[] List { get; set; } = monitors;
        public IReadOnlyList<MonitorInfo> Current() => List;
    }

    private static readonly MonitorInfo Primary = new(@"\\.\DISPLAY1", 0, 0, 1920, 1080, 1.25, true);
    private static readonly MonitorInfo Second = new(@"\\.\DISPLAY2", 1920, 0, 2560, 1440, 1.0, false);

    private static SmartDrawViewModel NewDraw(FakePresenter? presenter = null, FakeMonitors? monitors = null, ActivityLog? log = null)
    {
        DataFolderCollection.UseTemporaryFolder();
        return new SmartDrawViewModel(presenter ?? new FakePresenter(), monitors ?? new FakeMonitors(Primary, Second), log ?? new ActivityLog());
    }

    private static Annotation Pen(params double[] points) => new() { Kind = AnnotationKind.Pen, Monitor = Primary.Key, Points = [.. points], Thickness = 4 };

    private static Annotation Shape(AnnotationKind kind, double x1, double y1, double x2, double y2, string monitor = @"\\.\DISPLAY1") =>
        new() { Kind = kind, Monitor = monitor, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Thickness = 4 };

    // ----- Tools and geometry -----

    [Fact]
    public void ToolSelectionAndChoicesCycle()
    {
        var draw = NewDraw();
        Assert.Equal(DrawTool.Pen, draw.Tool);

        draw.SetToolCommand.Execute(DrawTool.Ellipse);
        Assert.Equal(DrawTool.Ellipse, draw.Tool);

        var first = draw.Thickness;
        draw.NextThicknessCommand.Execute(null);
        Assert.NotEqual(first, draw.Thickness);
        foreach (var _ in SmartDrawViewModel.Thicknesses) draw.NextThicknessCommand.Execute(null);
        Assert.Contains(draw.Thickness, SmartDrawViewModel.Thicknesses);

        draw.SetColorCommand.Execute("#FF38BDF8");
        Assert.Equal("#FF38BDF8", draw.Color);
        draw.ToggleArrowHeadCommand.Execute(null);
        Assert.Equal(ArrowHead.Open, draw.ArrowHead);
    }

    [Fact]
    public void PenStrokesSkipPointsThatAreTooClose()
    {
        var points = new List<double>();
        Assert.True(DrawingGeometry.AddPenPoint(points, new Point(10, 10)));
        Assert.False(DrawingGeometry.AddPenPoint(points, new Point(10.5, 10.5))); // < 1.5 px: not stored
        Assert.True(DrawingGeometry.AddPenPoint(points, new Point(14, 10)));
        Assert.Equal([10.0, 10, 14, 10], points);

        var document = new DrawingDocument();
        Assert.False(document.Add(Pen(5, 5)));           // a click without a drag
        Assert.True(document.Add(Pen(5, 5, 40, 40, 80, 20)));
        Assert.Single(document.Items);
    }

    [Fact]
    public void ArrowHeadPointsBackFromTheTip()
    {
        var (left, right) = DrawingGeometry.ArrowWings(new Point(0, 0), new Point(100, 0), thickness: 4);

        Assert.True(left.X < 100 && right.X < 100);                 // behind the tip
        Assert.Equal(left.X, right.X, 6);                            // symmetric
        Assert.Equal(-left.Y, right.Y, 6);
        Assert.Equal(16, DrawingGeometry.Distance(new Point(100, 0), left), 6); // length = max(12, 4 x thickness)
        Assert.True(DrawingGeometry.HitTest(Shape(AnnotationKind.Arrow, 0, 0, 100, 0), new Point(50, 2), 3));
        Assert.False(DrawingGeometry.HitTest(Shape(AnnotationKind.Arrow, 0, 0, 100, 0), new Point(50, 30), 3));
    }

    [Theory]
    [InlineData(10, 20, 110, 70)]  // down-right
    [InlineData(110, 70, 10, 20)]  // up-left
    [InlineData(110, 20, 10, 70)]  // down-left
    [InlineData(10, 70, 110, 20)]  // up-right
    public void RectanglesAreTheSameInEveryDragDirection(double x1, double y1, double x2, double y2)
    {
        Assert.Equal(new Rect(10, 20, 100, 50), DrawingGeometry.Normalize(new Point(x1, y1), new Point(x2, y2)));
        var rect = Shape(AnnotationKind.Rectangle, x1, y1, x2, y2);
        Assert.True(DrawingGeometry.HitTest(rect, new Point(10, 45), 2));   // on the left edge
        Assert.False(DrawingGeometry.HitTest(rect, new Point(60, 45), 2));  // inside, away from the lines
    }

    [Fact]
    public void EllipseBoundsAndCircleConstraint()
    {
        Assert.Equal(new Rect(0, 0, 80, 40), DrawingGeometry.EllipseBounds(new Point(0, 0), new Point(80, 40), circle: false));
        Assert.Equal(new Rect(0, 0, 80, 80), DrawingGeometry.EllipseBounds(new Point(0, 0), new Point(80, 40), circle: true));
        Assert.Equal(new Rect(20, 20, 80, 80), DrawingGeometry.EllipseBounds(new Point(100, 100), new Point(20, 60), circle: true)); // up-left

        var ellipse = Shape(AnnotationKind.Ellipse, 0, 0, 100, 50);
        Assert.True(DrawingGeometry.HitTest(ellipse, new Point(100, 25), 2));  // right end of the outline
        Assert.False(DrawingGeometry.HitTest(ellipse, new Point(50, 25), 2));  // centre
    }

    [Fact]
    public void TextAnnotationsNeedTextAndAreHitByTheirBounds()
    {
        var document = new DrawingDocument();
        var empty = new Annotation { Kind = AnnotationKind.Text, Monitor = Primary.Key, Text = "   " };
        Assert.False(document.Add(empty));

        var text = new Annotation { Kind = AnnotationKind.Text, Monitor = Primary.Key, Text = "Țintă aici → ăîâșț", X1 = 100, Y1 = 100, TextWidth = 200, TextHeight = 40, FontSize = 28 };
        Assert.True(document.Add(text));
        Assert.True(DrawingGeometry.HitTest(text, new Point(150, 120), 2));
        Assert.False(DrawingGeometry.HitTest(text, new Point(150, 200), 2));

        document.UpdateText(text, "Altceva", 120, 40);
        Assert.Equal("Altceva", text.Text);
        document.Undo();
        Assert.Equal("Țintă aici → ăîâșț", text.Text);
    }

    // ----- History, eraser, clear -----

    [Fact]
    public void UndoAndRedoFollowEveryKindOfChange()
    {
        var document = new DrawingDocument();
        var a = Pen(0, 0, 50, 50);
        var b = Shape(AnnotationKind.Rectangle, 0, 0, 40, 40);
        var c = Shape(AnnotationKind.Arrow, 0, 0, 90, 10);
        document.Add(a);
        document.Add(b);
        document.Add(c);
        document.Remove([b]);

        document.Undo();                           // b back, at its place
        Assert.Equal([a, b, c], document.Items);
        document.Undo();                           // c removed
        Assert.Equal([a, b], document.Items);
        document.Redo();
        Assert.Equal([a, b, c], document.Items);

        document.Add(Pen(1, 1, 30, 30));           // a new action drops the redo steps
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void HistoryIsBounded()
    {
        var document = new DrawingDocument();
        for (var i = 0; i < DrawingDocument.MaxHistory + 20; i++)
        {
            document.Add(Pen(0, i, 50, i));
        }

        var undone = 0;
        while (document.CanUndo)
        {
            document.Undo();
            undone++;
        }

        Assert.Equal(DrawingDocument.MaxHistory, undone);
        Assert.Equal(20, document.Count); // the oldest steps can no longer be undone
    }

    [Fact]
    public void EraserRemovesWhatItTouchesAsOneStep()
    {
        var document = new DrawingDocument();
        var line = Pen(0, 0, 100, 0);
        var box = Shape(AnnotationKind.Rectangle, 200, 200, 300, 300);
        var far = Shape(AnnotationKind.Ellipse, 500, 500, 600, 560);
        document.Add(line);
        document.Add(box);
        document.Add(far);

        var hit = document.Items.Where(x => DrawingGeometry.HitTest(x, new Point(50, 3), 6) || DrawingGeometry.HitTest(x, new Point(200, 250), 6)).ToList();
        Assert.Equal(2, document.Remove(hit));

        Assert.Equal([far], document.Items);
        document.Undo();
        Assert.Equal(3, document.Count);
    }

    [Fact]
    public void ClearAsksFirstAndCanBeUndone()
    {
        var log = new ActivityLog();
        var draw = NewDraw(log: log);
        draw.Document.Add(Pen(0, 0, 60, 60));
        draw.Document.Add(Shape(AnnotationKind.Rectangle, 0, 0, 50, 50));

        draw.RequestClear();
        Assert.True(draw.IsConfirmingClear);
        Assert.Equal(2, draw.Document.Count);        // nothing removed yet
        draw.RequestClear();                         // second press (hotkey) confirms
        Assert.Equal(0, draw.Document.Count);
        Assert.False(draw.IsConfirmingClear);

        draw.Document.Undo();
        Assert.Equal(2, draw.Document.Count);

        draw.RequestClear();
        draw.CancelClearCommand.Execute(null);
        Assert.Equal(2, draw.Document.Count);
    }

    [Fact]
    public void DrawingIsBounded()
    {
        var document = new DrawingDocument();
        for (var i = 0; i < DrawingDocument.MaxAnnotations; i++)
        {
            Assert.True(document.Add(Pen(0, i, 10, i)));
        }

        Assert.True(document.IsFull);
        Assert.False(document.Add(Pen(0, 0, 10, 10)));
    }

    // ----- Modes -----

    [Fact]
    public void EnablingOpensTheWindowsInDrawModeAndDisablingClosesThem()
    {
        var presenter = new FakePresenter();
        var draw = NewDraw(presenter);
        Assert.True(draw.IsClickThrough); // off: nothing takes the mouse

        draw.ToggleEnabled();
        Assert.True(presenter.IsOpen);
        Assert.True(draw.IsDrawMode);
        Assert.False(draw.IsClickThrough);

        draw.CloseCommand.Execute(null);
        Assert.False(presenter.IsOpen);
        Assert.Equal(1, presenter.Closed);
        Assert.True(draw.IsClickThrough);
    }

    [Fact]
    public void ModeChangesSwitchClickThrough()
    {
        var draw = NewDraw();
        draw.ToggleMode();                          // off → on, in draw mode
        Assert.True(draw.IsEnabled);
        Assert.False(draw.IsClickThrough);

        draw.ToggleMode();                          // → pass-through
        Assert.False(draw.IsDrawMode);
        Assert.True(draw.IsClickThrough);

        draw.Tool = DrawTool.Arrow;                 // picking a tool means drawing
        Assert.True(draw.IsDrawMode);

        draw.ToggleVisibility();                    // hidden drawings never take clicks
        Assert.False(draw.AnnotationsVisible);
        Assert.True(draw.IsClickThrough);
        draw.SetDrawMode(true);                     // drawing shows them again
        Assert.True(draw.AnnotationsVisible);
        Assert.False(draw.IsClickThrough);
    }

    [Fact]
    public void DisposeReleasesTheWindows()
    {
        var presenter = new FakePresenter();
        var draw = NewDraw(presenter);
        draw.IsEnabled = true;

        draw.Dispose();

        Assert.False(presenter.IsOpen);
        Assert.Equal(1, presenter.Opened);
        Assert.Equal(1, presenter.Closed);
    }

    // ----- Persistence and monitors -----

    [Fact]
    public void SaveAndRestoreRoundTripVectorData()
    {
        var draw = NewDraw();
        var stroke = Pen(10, 10, 40, 60, 90, 20);
        stroke.Color = "#FF34D399";
        draw.Document.Add(stroke);
        draw.Document.Add(new Annotation { Kind = AnnotationKind.Text, Monitor = Second.Key, Text = "Săgeată ↓", X1 = 5, Y1 = 6, FontSize = 30, TextWidth = 120, TextHeight = 40 });
        draw.Document.Add(Shape(AnnotationKind.Arrow, 0, 0, 300, 200, Second.Key));
        draw.Save();
        Assert.True(draw.HasSavedDrawing);

        draw.Document.Clear();
        draw.Restore();

        Assert.Equal(3, draw.Document.Count);
        var restored = draw.Document.Items[0];
        Assert.Equal([10.0, 10, 40, 60, 90, 20], restored.Points);
        Assert.Equal("#FF34D399", restored.Color);
        Assert.Equal("Săgeată ↓", draw.Document.Items[1].Text);
        Assert.False(draw.Document.CanUndo); // a restored drawing starts a new history
    }

    [Fact]
    public void RestoreWithoutASavedDrawingOrFromACorruptFileSaysSo()
    {
        var draw = NewDraw();
        draw.Restore();
        Assert.Equal("Draw.NothingSaved", draw.ErrorMessage);

        File.WriteAllText(draw.StorageLocation, "{ \"annotations\": [ { \"kind\": ");
        draw.Restore();
        Assert.NotNull(draw.ErrorMessage);
        Assert.Equal(0, draw.Document.Count);
        Assert.True(File.Exists(draw.StorageLocation + ".corrupt"));
    }

    [Fact]
    public void SavedDrawingsFollowMonitorsThatMovedChangedResolutionOrWereDisconnected()
    {
        var monitors = new FakeMonitors(Primary, Second);
        var draw = NewDraw(monitors: monitors);
        draw.Document.Add(Shape(AnnotationKind.Rectangle, 100, 100, 500, 400, Second.Key));
        draw.Document.Add(Shape(AnnotationKind.Ellipse, 50, 50, 250, 150, Primary.Key));
        draw.Save();

        // DISPLAY2 now on the left at 1280x720; DISPLAY1 unchanged.
        monitors.List = [Primary, Second with { Left = -1280, Width = 1280, Height = 720 }];
        draw.Restore();
        var moved = draw.Document.Items.Single(a => a.Kind == AnnotationKind.Rectangle);
        Assert.Equal(Second.Key, moved.Monitor);
        Assert.Equal(50, moved.X1, 6);   // 100 x 1280/2560
        Assert.Equal(50, moved.Y1, 6);   // 100 x 720/1440
        Assert.Equal(2, moved.Thickness, 6);
        var same = draw.Document.Items.Single(a => a.Kind == AnnotationKind.Ellipse);
        Assert.Equal(50, same.X1);       // monitor relative: unaffected by DISPLAY2 moving

        // DISPLAY2 disconnected: its drawing goes to the primary monitor, scaled to it.
        monitors.List = [Primary];
        draw.Restore();
        var fallback = draw.Document.Items.Single(a => a.Kind == AnnotationKind.Rectangle);
        Assert.Equal(Primary.Key, fallback.Monitor);
        Assert.Equal(75, fallback.X1, 6); // 100 x 1920/2560
        Assert.Equal(75, fallback.Y1, 6); // 100 x 1080/1440
    }

    [Fact]
    public void MonitorResolutionFallsBackToThePrimary()
    {
        IReadOnlyList<MonitorInfo> current = [Second, Primary];
        Assert.Equal(Second, MonitorMapping.Resolve(Second.Key, current));
        Assert.Equal(Primary, MonitorMapping.Resolve(@"\\.\DISPLAY9", current));
        Assert.Null(MonitorMapping.Resolve(Primary.Key, []));
    }

    [Fact]
    public void SmartDrawHotkeysHaveFreeDefaultsAndConflictsAreDetected() => OnSta(() =>
    {
        using var hotkeys = new HotkeyService();
        var draw = new[] { HotkeyAction.ToggleSmartDraw, HotkeyAction.DrawMode, HotkeyAction.ToggleAnnotations, HotkeyAction.ClearAnnotations }
            .Select(a => hotkeys.Bindings.Single(b => b.Action == a)).ToList();

        Assert.Equal(hotkeys.Bindings.Count, hotkeys.Bindings.Select(b => b.Current).Distinct().Count()); // no clash with overlays or notes
        Assert.Equal(["Ctrl + Shift + F7", "Ctrl + Shift + F6", "Ctrl + Shift + F5", "Ctrl + Shift + F4"], draw.Select(b => b.Gesture));

        hotkeys.SetEnabled(true);
        var notes = hotkeys.Bindings.Single(b => b.Action == HotkeyAction.ToggleNotes);
        Assert.Equal(HotkeyStatus.Conflict, hotkeys.ChangeGesture(draw[1], notes.Current));
    });

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
}
