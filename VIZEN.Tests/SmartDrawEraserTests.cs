using System.Windows;
using VIZEN.App.Models;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.Tests;

/// <summary>Smart Draw precision and object eraser (pure geometry and document history).</summary>
[Collection(DataFolderCollection.Name)]
public class SmartDrawEraserTests
{
    private const string Monitor = @"\\.\DISPLAY1";

    private static Annotation Stroke(double thickness, params double[] points) =>
        new() { Kind = AnnotationKind.Pen, Monitor = Monitor, Points = [.. points], Thickness = thickness, Color = "#FF38BDF8" };

    private static Annotation Line(double x1, double y1, double x2, double y2, double thickness = 4, int segments = 10)
    {
        var points = new List<double>();
        for (var i = 0; i <= segments; i++)
        {
            points.Add(x1 + (x2 - x1) * i / segments);
            points.Add(y1 + (y2 - y1) * i / segments);
        }

        return Stroke(thickness, [.. points]);
    }

    private static Point First(Annotation a) => new(a.Points[0], a.Points[1]);
    private static Point Last(Annotation a) => new(a.Points[^2], a.Points[^1]);

    private static double Length(Annotation a)
    {
        var length = 0.0;
        for (var i = 2; i + 1 < a.Points.Count; i += 2)
        {
            length += DrawingGeometry.Distance(new Point(a.Points[i - 2], a.Points[i - 1]), new Point(a.Points[i], a.Points[i + 1]));
        }

        return length;
    }

    [Fact]
    public void PrecisionEraserCutsAStraightStrokeInTwoAndKeepsItsLook()
    {
        var stroke = Line(0, 100, 200, 100, thickness: 4);
        var gesture = new EraseGesture([stroke], EraserMode.Precision);

        Assert.True(gesture.Apply(new Point(100, 100), new Point(100, 100), radius: 10));

        var pieces = gesture.Fragments.OrderBy(f => f.Points[0]).ToList();
        Assert.Equal(2, pieces.Count);
        // Erased reach = radius + half the line width: the remaining round ends just touch the eraser's circle.
        Assert.Equal(88, Last(pieces[0]).X, 1);
        Assert.Equal(112, First(pieces[1]).X, 1);
        Assert.Equal(new Point(0, 100), First(pieces[0]));
        Assert.Equal(new Point(200, 100), Last(pieces[1]));
        Assert.All(pieces, p =>
        {
            Assert.Equal(4, p.Thickness);
            Assert.Equal("#FF38BDF8", p.Color);
            Assert.Equal(Monitor, p.Monitor);
            Assert.NotEqual(stroke.Id, p.Id);
        });
        Assert.Equal(22, stroke.Points.Count); // the original is untouched until the drag is committed
    }

    [Fact]
    public void CurvedStrokesAreCutExactlyAtTheEraserEdge()
    {
        // Upper half of a circle (radius 50 around 100,100), 60 segments.
        var points = new List<double>();
        for (var i = 0; i <= 60; i++)
        {
            var angle = Math.PI * i / 60;
            points.Add(100 + 50 * Math.Cos(angle));
            points.Add(100 - 50 * Math.Sin(angle));
        }

        var arc = Stroke(2, [.. points]);
        var gesture = new EraseGesture([arc], EraserMode.Precision);
        gesture.Apply(new Point(100, 0), new Point(100, 200), radius: 6); // vertical sweep through the top of the arc

        var pieces = gesture.Fragments.ToList();
        Assert.Equal(2, pieces.Count);
        var cutEnds = pieces.Select(p => Math.Abs(First(p).X - 100) < Math.Abs(Last(p).X - 100) ? First(p) : Last(p)).ToList();
        Assert.All(cutEnds, end => Assert.Equal(7, Math.Abs(end.X - 100), 1)); // reach 6 + 1
        Assert.All(pieces, p => Assert.True(p.Points.Count >= 4));
    }

    [Fact]
    public void AFastMoveErasesEverythingAlongItsPath()
    {
        // Five vertical strokes, then one big jump of the mouse across all of them (no intermediate events).
        var strokes = Enumerable.Range(1, 5).Select(i => Line(i * 30, 0, i * 30, 100, thickness: 2)).ToList();
        var gesture = new EraseGesture(strokes, EraserMode.Precision);

        gesture.Apply(new Point(0, 50), new Point(200, 50), radius: 3);

        var pieces = gesture.Fragments.ToList();
        Assert.Equal(5, gesture.CutStrokes.Count());
        Assert.Equal(10, pieces.Count);
        Assert.All(pieces, p =>
        {
            var cut = Math.Abs(First(p).Y - 50) < Math.Abs(Last(p).Y - 50) ? First(p) : Last(p);
            Assert.Equal(4, Math.Abs(cut.Y - 50), 1); // no un-erased gap left anywhere on the path
        });
    }

    [Theory]
    [InlineData(2.5, 9)]    // 5 DIP eraser: gap 2 x (2.5 + 2)
    [InlineData(10, 24)]
    [InlineData(50, 104)]   // 100 DIP eraser
    public void TheGapMatchesTheEraserSize(double radius, double gap)
    {
        var stroke = Line(0, 0, 300, 0, thickness: 4, segments: 30);
        var gesture = new EraseGesture([stroke], EraserMode.Precision);
        gesture.Apply(new Point(150, 0), new Point(150, 0), radius);

        var pieces = gesture.Fragments.OrderBy(f => f.Points[0]).ToList();
        Assert.Equal(2, pieces.Count);
        Assert.Equal(gap, First(pieces[1]).X - Last(pieces[0]).X, 1);
        Assert.Equal(300 - gap, Length(pieces[0]) + Length(pieces[1]), 1);
    }

    [Fact]
    public void AnEraserBiggerThanTheStrokeRemovesItAllAndLeavesNoDots()
    {
        var stroke = Line(0, 0, 40, 0);
        var gesture = new EraseGesture([stroke], EraserMode.Precision);
        gesture.Apply(new Point(20, 0), new Point(20, 0), radius: 50);
        Assert.Single(gesture.CutStrokes);
        Assert.Empty(gesture.Fragments);

        var nearEnd = Line(0, 0, 100, 0);
        var trim = new EraseGesture([nearEnd], EraserMode.Precision);
        trim.Apply(new Point(96, 0), new Point(96, 0), radius: 10);
        var piece = Assert.Single(trim.Fragments);    // the end is trimmed; no stray piece beyond it
        Assert.Equal(84, Last(piece).X, 1);
    }

    [Fact]
    public void NearbyStrokesAreLeftAlone()
    {
        var erased = Line(0, 100, 200, 100);
        var neighbour = Line(0, 125, 200, 125); // 25 px away; the eraser reaches 12
        var gesture = new EraseGesture([erased, neighbour], EraserMode.Precision);
        gesture.Apply(new Point(0, 100), new Point(200, 100), radius: 10);

        Assert.Equal([erased], gesture.CutStrokes);

        var document = new DrawingDocument();
        document.Add(erased);
        document.Add(neighbour);
        gesture.Commit(document);
        Assert.Equal([neighbour], document.Items); // the same object, untouched
    }

    [Fact]
    public void OneDragIsOneUndoStepAndRedoWorks()
    {
        var document = new DrawingDocument();
        var stroke = Line(0, 50, 300, 50, segments: 30);
        document.Add(stroke);

        var gesture = new EraseGesture(document.Items, EraserMode.Precision);
        gesture.Apply(new Point(60, 50), new Point(60, 50), 5);   // mouse down
        gesture.Apply(new Point(60, 50), new Point(60, 40), 5);   // moves
        gesture.Apply(new Point(150, 0), new Point(150, 90), 5);  // a second cut, same drag
        gesture.Apply(new Point(240, 50), new Point(240, 50), 5); // a third
        Assert.True(gesture.Commit(document));

        Assert.Equal(4, document.Count);
        document.Undo();
        Assert.Equal([stroke], document.Items);    // the original stroke, unchanged
        document.Redo();
        Assert.Equal(4, document.Count);
        document.Undo();
        document.Undo();                           // the step before: drawing the stroke
        Assert.Equal(0, document.Count);
    }

    [Fact]
    public void PiecesKeepTheStackingPositionOfTheirStroke()
    {
        var document = new DrawingDocument();
        var below = Line(0, 0, 100, 0);
        var cut = Line(0, 50, 100, 50);
        var above = Line(0, 100, 100, 100);
        document.Add(below);
        document.Add(cut);
        document.Add(above);

        var gesture = new EraseGesture(document.Items, EraserMode.Precision);
        gesture.Apply(new Point(50, 50), new Point(50, 50), 5);
        gesture.Commit(document);

        Assert.Equal(4, document.Count);
        Assert.Same(below, document.Items[0]);
        Assert.Same(above, document.Items[3]);
    }

    [Fact]
    public void ShapesAndTextAreRemovedWholeEvenInPrecisionMode()
    {
        var rectangle = new Annotation { Kind = AnnotationKind.Rectangle, Monitor = Monitor, X1 = 100, Y1 = 100, X2 = 200, Y2 = 160, Thickness = 4 };
        var ellipse = new Annotation { Kind = AnnotationKind.Ellipse, Monitor = Monitor, X1 = 400, Y1 = 400, X2 = 500, Y2 = 450, Thickness = 4 };
        var text = new Annotation { Kind = AnnotationKind.Text, Monitor = Monitor, Text = "Țintă", X1 = 300, Y1 = 100, TextWidth = 80, TextHeight = 30, FontSize = 24 };
        var gesture = new EraseGesture([rectangle, ellipse, text], EraserMode.Precision);

        gesture.Apply(new Point(90, 130), new Point(340, 115), radius: 6); // across the rectangle's left edge and the text

        Assert.Equal(new[] { rectangle, text }.ToHashSet(), gesture.WholeRemoved.ToHashSet());
        Assert.Empty(gesture.Fragments);
    }

    [Fact]
    public void ObjectEraserRemovesTouchedStrokesWhole()
    {
        var document = new DrawingDocument();
        var touched = Line(0, 0, 200, 0);
        var other = Line(0, 80, 200, 80);
        document.Add(touched);
        document.Add(other);

        var gesture = new EraseGesture(document.Items, EraserMode.Object);
        gesture.Apply(new Point(100, -5), new Point(100, 5), 4);

        Assert.Empty(gesture.Fragments);
        Assert.Equal([touched], gesture.WholeRemoved);
        gesture.Commit(document);
        Assert.Equal([other], document.Items);
        document.Undo();
        Assert.Equal(2, document.Count);
    }

    [Fact]
    public void NothingTouchedChangesNothing()
    {
        var document = new DrawingDocument();
        document.Add(Line(0, 0, 100, 0));
        var gesture = new EraseGesture(document.Items, EraserMode.Precision);

        Assert.False(gesture.Apply(new Point(500, 500), new Point(600, 600), 20));
        Assert.False(gesture.Commit(document));
        Assert.True(document.CanUndo); // only the drawing step
        document.Undo();
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void EraserSettingsDefaultToPrecisionAndStayInRange()
    {
        DataFolderCollection.UseTemporaryFolder();
        var draw = new SmartDrawViewModel(new NoWindows(), new NoMonitors(), new ActivityLog());
        Assert.Equal(EraserMode.Precision, draw.EraserMode);
        Assert.Equal(SmartDrawViewModel.DefaultEraserSize, draw.EraserSize);

        draw.EraserSize = 2;
        Assert.Equal(5, draw.EraserSize);
        draw.EraserSize = 250;
        Assert.Equal(100, draw.EraserSize);
        draw.ToggleEraserModeCommand.Execute(null);
        Assert.Equal(EraserMode.Object, draw.EraserMode);
    }

    private sealed class NoWindows : ISmartDrawPresenter
    {
        public bool IsOpen => false;
        public void Open(SmartDrawViewModel drawing) { }
        public void Close() { }
    }

    private sealed class NoMonitors : IMonitorProvider
    {
        public IReadOnlyList<MonitorInfo> Current() => [];
    }
}
