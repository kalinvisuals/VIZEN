using System.Windows;
using VIZEN.App.Models;

namespace VIZEN.Tests;

/// <summary>Frame-synchronized eraser sessions, localized change reporting and targeted document updates.</summary>
public class SmartDrawEraserBatchingTests
{
    private const string Monitor = @"\\.\DISPLAY1";

    /// <summary>A rendering callback driven by the test: <see cref="Tick"/> is one rendering pass.</summary>
    private sealed class FakeClock : IFrameClock
    {
        private readonly List<EventHandler> _handlers = [];
        public int Subscribed { get; private set; }
        public int Unsubscribed { get; private set; }
        public bool HasHandler => _handlers.Count > 0;
        public void Subscribe(EventHandler handler) { _handlers.Add(handler); Subscribed++; }
        public void Unsubscribe(EventHandler handler) { _handlers.Remove(handler); Unsubscribed++; }
        public void Tick() { foreach (var h in _handlers.ToList()) h(this, EventArgs.Empty); }
    }

    private static Annotation Line(double x1, double y1, double x2, double y2, double thickness = 4, int segments = 20)
    {
        var points = new List<double>();
        for (var i = 0; i <= segments; i++)
        {
            points.Add(x1 + (x2 - x1) * i / segments);
            points.Add(y1 + (y2 - y1) * i / segments);
        }

        return new Annotation { Kind = AnnotationKind.Pen, Monitor = Monitor, Points = points, Thickness = thickness, Color = "#FFFFE94D" };
    }

    private static DrawingDocument Grid(int count, double spacing = 40)
    {
        var document = new DrawingDocument();
        for (var i = 0; i < count; i++)
        {
            document.Add(Line(0, 100 + i * spacing, 800, 100 + i * spacing, segments: 40));
        }

        return document;
    }

    /// <summary>A zig-zag eraser path with many small mouse steps.</summary>
    private static List<Point> ZigZag()
    {
        var path = new List<Point>();
        for (var i = 0; i <= 400; i++)
        {
            path.Add(new Point(i * 2, 80 + (i % 50 < 25 ? i % 25 : 25 - i % 25) * 14));
        }

        return path;
    }

    /// <summary>Runs a drag through a session, ticking the frame clock after every <paramref name="movesPerFrame"/> moves.</summary>
    private static DrawingDocument Erase(DrawingDocument document, IReadOnlyList<Point> path, double radius, int movesPerFrame, FakeClock? clock = null)
    {
        clock ??= new FakeClock();
        var session = new EraseSession(new EraseGesture(document.Items, EraserMode.Precision), path[0], radius, clock);
        session.Start();
        for (var i = 1; i < path.Count; i++)
        {
            session.MoveTo(path[i]);
            if (movesPerFrame > 0 && i % movesPerFrame == 0)
            {
                clock.Tick();
            }
        }

        session.Finish(document, commit: true);
        return document;
    }

    private static List<List<double>> Geometry(DrawingDocument document) => document.Items.Select(a => a.Points.ToList()).ToList();

    [Theory]
    [InlineData(1)]   // a rendering pass after every mouse event
    [InlineData(4)]   // ~240 Hz mouse on a 60 Hz pass
    [InlineData(17)]  // ~1000 Hz mouse on a 60 Hz pass
    [InlineData(0)]   // no pass at all until the button is released
    public void BatchingGivesTheSameGeometryAtAnyRenderingRate(int movesPerFrame)
    {
        var reference = Erase(Grid(8), ZigZag(), radius: 9, movesPerFrame: 1);
        var batched = Erase(Grid(8), ZigZag(), radius: 9, movesPerFrame);

        var expected = Geometry(reference);
        var actual = Geometry(batched);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Count, actual[i].Count);
            for (var k = 0; k < expected[i].Count; k++)
            {
                Assert.Equal(expected[i][k], actual[i][k], 9);
            }
        }
    }

    [Fact]
    public void ManyPositionsPerFrameAreAllUsedAndProcessedOnce()
    {
        var clock = new FakeClock();
        var document = Grid(3);
        var session = new EraseSession(new EraseGesture(document.Items, EraserMode.Precision), new Point(0, 0), 5, clock);
        session.Start();
        foreach (var x in Enumerable.Range(1, 100))
        {
            session.MoveTo(new Point(x * 8, x * 2.4)); // 100 mouse events before one rendering pass
        }

        clock.Tick();
        Assert.Equal(1, session.FramesProcessed);
        Assert.Equal(100, session.PositionsProcessed);
        Assert.Equal(3, session.Gesture.CutStrokes.Count()); // the diagonal path crossed all three lines
    }

    [Fact]
    public void FastMovesWithLargeJumpsLeaveNoGaps()
    {
        var document = new DrawingDocument();
        for (var i = 0; i < 6; i++)
        {
            document.Add(Line(100 + i * 100, 0, 100 + i * 100, 400, thickness: 3));
        }

        // Three mouse positions, 300 px apart, in one rendering pass.
        Erase(document, [new Point(50, 200), new Point(350, 200), new Point(700, 200)], radius: 4, movesPerFrame: 0);

        Assert.Equal(12, document.Count);
        Assert.All(document.Items, a =>
        {
            var nearest = Math.Min(Math.Abs(a.Points[1] - 200), Math.Abs(a.Points[^1] - 200));
            Assert.Equal(5.5, nearest, 1); // radius 4 + half of 3
        });
    }

    [Fact]
    public void NothingWithinReachOfThePathSurvives()
    {
        var path = ZigZag();
        var document = Erase(Grid(10, spacing: 30), path, radius: 7, movesPerFrame: 5);

        foreach (var a in document.Items)
        {
            var reach = 7 + a.Thickness / 2;
            for (var i = 0; i + 1 < a.Points.Count; i += 2)
            {
                var p = new Point(a.Points[i], a.Points[i + 1]);
                for (var k = 1; k < path.Count; k++)
                {
                    Assert.True(DrawingGeometry.DistanceToSegment(p, path[k - 1], path[k]) >= reach - 0.01,
                        $"a remaining point at {p} is inside the eraser's path");
                }
            }
        }
    }

    [Fact]
    public void LongStrokesLoseOnlyTheTouchedPart()
    {
        var document = new DrawingDocument();
        var stroke = Line(0, 500, 3000, 500, thickness: 6, segments: 300);
        document.Add(stroke);

        Erase(document, [new Point(1500, 470), new Point(1500, 530)], radius: 10, movesPerFrame: 1);

        Assert.Equal(2, document.Count);
        var kept = document.Items.Sum(a => a.Points[^2] - a.Points[0]);
        Assert.Equal(3000 - 26, kept, 1); // gap = 2 x (10 + 3)
    }

    [Fact]
    public void CurvedStrokesAreCutInBatchesToo()
    {
        var points = new List<double>();
        for (var i = 0; i <= 90; i++)
        {
            var angle = 2 * Math.PI * i / 90;
            points.Add(300 + 100 * Math.Cos(angle));
            points.Add(300 + 100 * Math.Sin(angle));
        }

        var document = new DrawingDocument();
        document.Add(new Annotation { Kind = AnnotationKind.Pen, Monitor = Monitor, Points = points, Thickness = 2 });

        // A horizontal sweep through the circle's middle cuts it on the left and the right.
        Erase(document, [new Point(150, 300), new Point(250, 300), new Point(350, 300), new Point(450, 300)], radius: 5, movesPerFrame: 2);

        Assert.Equal(2, document.Count);
        Assert.All(document.Items, a => Assert.True(a.Points.Count >= 6));
    }

    [Fact]
    public void NearbyStrokesAreNotTouchedAndNotReported()
    {
        var document = new DrawingDocument();
        var erased = Line(0, 100, 600, 100);
        var close = Line(0, 118, 600, 118); // 18 px away; reach 8 + 2 = 10
        document.Add(erased);
        document.Add(close);
        var clock = new FakeClock();
        var frames = new List<EraseFrame>();
        var session = new EraseSession(new EraseGesture(document.Items, EraserMode.Precision), new Point(0, 100), 8, clock);
        session.FrameReady += frames.Add;
        session.Start();
        session.MoveTo(new Point(600, 100));
        clock.Tick();
        session.Finish(document, commit: true);

        Assert.DoesNotContain(frames.SelectMany(f => f.Removed.Concat(f.Added)), a => a == close);
        Assert.Contains(close, document.Items);
        Assert.DoesNotContain(erased, document.Items);
    }

    [Fact]
    public void EachFrameReportsOnlyWhatChangedAndItsRegion()
    {
        var document = new DrawingDocument();
        var stroke = Line(0, 200, 1000, 200, thickness: 4, segments: 100);
        document.Add(stroke);
        var clock = new FakeClock();
        var frames = new List<EraseFrame>();
        var session = new EraseSession(new EraseGesture(document.Items, EraserMode.Precision), new Point(300, 200), 10, clock);
        session.FrameReady += frames.Add;

        session.Start(); // first cut at x = 300
        var first = Assert.Single(frames);
        Assert.Equal([stroke], first.Removed);       // the original goes out once
        Assert.Equal(2, first.Added.Count);
        Assert.True(EraserGeometry.AnnotationBounds(stroke).Contains(first.Dirty));
        Assert.True(first.Dirty.Contains(new Point(300, 200)));

        session.MoveTo(new Point(300, 260));       // second frame: away from the line, a jump, then across it at x = 800
        session.MoveTo(new Point(800, 260));
        session.MoveTo(new Point(800, 190));
        clock.Tick();
        Assert.Equal(2, frames.Count);
        var second = frames[1];
        var rightPiece = Assert.Single(second.Removed);
        Assert.Contains(rightPiece, first.Added);   // a piece from the first cut, not the original
        Assert.True(rightPiece.Points[0] > 300);
        Assert.DoesNotContain(first.Added.Single(p => p != rightPiece), second.Removed.Concat(second.Added)); // left piece untouched
        Assert.True(second.Dirty.Left > 300);       // the region does not reach back to the first cut
        Assert.True(second.Dirty.Width < first.Dirty.Width);

        clock.Tick();                               // nothing new: no frame, callback released
        Assert.Equal(2, frames.Count);
        Assert.False(session.IsSubscribed);
        session.Finish(document, commit: true);
        Assert.Equal(3, document.Count);
    }

    [Fact]
    public void UndoAndRedoAfterABatchedDrag()
    {
        var document = Grid(4);
        var before = document.Items.ToList();
        Erase(document, ZigZag(), radius: 8, movesPerFrame: 9);
        var after = document.Items.ToList();
        Assert.NotEqual(before.Count, after.Count);

        document.Undo();
        Assert.Equal(before, document.Items);
        document.Redo();
        Assert.Equal(after, document.Items);
    }

    [Fact]
    public void TheFrameCallbackIsOnlySubscribedWhileThereIsWork()
    {
        var clock = new FakeClock();
        var document = Grid(2);
        var session = new EraseSession(new EraseGesture(document.Items, EraserMode.Precision), new Point(0, 100), 5, clock);
        session.Start();
        Assert.False(clock.HasHandler);              // pressing does not need a callback

        session.MoveTo(new Point(10, 100));
        Assert.True(session.IsSubscribed);
        session.MoveTo(new Point(20, 100));
        Assert.Equal(1, clock.Subscribed);          // once, however many positions arrive

        clock.Tick();                               // processed
        Assert.True(session.IsSubscribed);
        clock.Tick();                               // mouse still: released
        Assert.False(clock.HasHandler);

        session.MoveTo(new Point(30, 100));         // moving again subscribes again
        Assert.True(clock.HasHandler);
        session.Finish(document, commit: true);     // the end of the drag always releases it
        Assert.False(clock.HasHandler);
        Assert.Equal(clock.Subscribed, clock.Unsubscribed);
    }

    [Fact]
    public void FinishingEarlyIsSafeAndFinal()
    {
        // e.g. another tool picked, or draw mode left, in the middle of a drag
        var clock = new FakeClock();
        var document = Grid(2);
        var session = new EraseSession(new EraseGesture(document.Items, EraserMode.Precision), new Point(400, 0), 6, clock);
        session.Start();
        session.MoveTo(new Point(400, 300));        // queued, not yet processed

        Assert.True(session.Finish(document, commit: true)); // the queued move is still erased and committed
        Assert.Equal(4, document.Count);
        Assert.False(session.Finish(document, commit: true));
        session.MoveTo(new Point(0, 0));
        Assert.False(clock.HasHandler);
        session.Dispose();
        Assert.Equal(4, document.Count);
    }

    [Fact]
    public void CancelledDragLeavesTheDocumentAlone()
    {
        var document = Grid(2);
        var before = document.Items.ToList();
        var session = new EraseSession(new EraseGesture(document.Items, EraserMode.Precision), new Point(400, 0), 6, new FakeClock());
        session.Start();
        session.MoveTo(new Point(400, 300));
        Assert.False(session.Finish(document, commit: false));
        Assert.Equal(before, document.Items);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void TheCircleOnScreenAndTheErasedAreaMatchAtAnyScaling(double dpi)
    {
        const double diameterDip = 24;
        var offset = new Vector(0, 40 * dpi); // a work area starting below a top taskbar
        var centreDip = new Point(500, 300);
        var edgeDip = new Point(500 + diameterDip / 2, 300);

        var centre = DrawCoordinates.ToMonitor(centreDip, dpi, offset);
        var edge = DrawCoordinates.ToMonitor(edgeDip, dpi, offset);

        Assert.Equal(DrawCoordinates.EraserRadius(diameterDip, dpi), DrawingGeometry.Distance(centre, edge), 9);
        Assert.Equal(new Point(500 * dpi, 300 * dpi + 40 * dpi), centre);
    }

    [Fact]
    public void ReplacingRaisesOnlyTheAnnotationsInvolved()
    {
        var document = Grid(3);
        var middle = document.Items[1];
        var changes = new List<DrawingChange>();
        document.Changed += (_, c) => changes.Add(c);

        Erase(document, [new Point(400, 130), new Point(400, 150)], radius: 5, movesPerFrame: 1);

        var change = Assert.Single(changes);
        Assert.Equal(DrawingChangeKind.Replaced, change.Kind);  // not a full Reset
        Assert.Equal([middle], change.Removed);
        Assert.Equal(2, change.Annotations.Count);

        document.Undo();
        Assert.Equal(DrawingChangeKind.Replaced, changes[^1].Kind);
        Assert.Equal([middle], changes[^1].Annotations);
        Assert.Equal(2, changes[^1].Removed!.Count);
    }
}

/// <summary>
/// Redraw work of the eraser preview on the benchmark's workload, before vs after the optimization: the old preview
/// redrew every piece cut since the start of the drag on every mouse event; the new one redraws only the pieces
/// that changed, once per rendering pass. Counts are deterministic (no timing), printed for the report.
/// </summary>
public class SmartDrawEraserRedrawWorkTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static DrawingDocument BenchmarkStrokes()
    {
        // The 25 strokes drawn in drawregress.ps1 phase C (rows of a sine wave, 30 points each).
        var document = new DrawingDocument();
        for (var s = 0; s < 25; s++)
        {
            var y = 120 + (s % 8) * 25;
            var x0 = s % 2 == 1 ? 950 : 300;
            var points = new List<double>();
            for (var i = 0; i <= 30; i++)
            {
                points.Add(x0 + i * 30);
                points.Add(y + Math.Round(40 * Math.Sin(i / 3.0)));
            }

            document.Add(new Annotation { Kind = AnnotationKind.Pen, Monitor = "M", Points = points, Thickness = 4 * 1.33 });
        }

        return document;
    }

    /// <summary>The eraser drags of phase E, sampled at <paramref name="mouseHz"/> (30 steps of ~16 ms per drag).</summary>
    private static List<List<Point>> EraserDrags(int mouseHz)
    {
        var perStep = Math.Max(1, mouseHz / 60);
        var drags = new List<List<Point>>();
        for (var s = 0; s < 25; s++)
        {
            var y = 120 + (s % 8) * 25;
            var x0 = s % 2 == 1 ? 950 : 300;
            var path = new List<Point> { new(x0, y) };
            for (var i = 1; i <= 30; i++)
            {
                var a = path[^1];
                var b = new Point(x0 + i * 30, y + Math.Round(40 * Math.Sin(i / 3.0)));
                for (var k = 1; k <= perStep; k++)
                {
                    path.Add(new Point(a.X + (b.X - a.X) * k / perStep, a.Y + (b.Y - a.Y) * k / perStep));
                }
            }

            drags.Add(path);
        }

        return drags;
    }

    private static (long Rendered, double Area, long Calls) Old(int mouseHz, double radius)
    {
        var document = BenchmarkStrokes();
        long rendered = 0, calls = 0;
        double area = 0;
        foreach (var drag in EraserDrags(mouseHz))
        {
            var gesture = new EraseGesture(document.Items, EraserMode.Precision);
            for (var i = 0; i < drag.Count; i++)
            {
                // Old preview: on every mouse event that changed something, all pieces so far were redrawn.
                if (gesture.Apply(drag[Math.Max(0, i - 1)], drag[i], radius))
                {
                    var pieces = gesture.Fragments.ToList();
                    rendered += pieces.Count;
                    calls++;
                    var bounds = Rect.Empty;
                    pieces.ForEach(p => bounds.Union(EraserGeometry.AnnotationBounds(p)));
                    area += bounds.IsEmpty ? 0 : bounds.Width * bounds.Height;
                }
            }

            gesture.Commit(document);
        }

        return (rendered, area, calls);
    }

    private sealed class Clock : IFrameClock
    {
        private EventHandler? _h;
        public void Subscribe(EventHandler handler) => _h += handler;
        public void Unsubscribe(EventHandler handler) => _h -= handler;
        public void Tick() => _h?.Invoke(this, EventArgs.Empty);
    }

    private static (long Rendered, double Area, long Calls) New(int mouseHz, double radius)
    {
        var document = BenchmarkStrokes();
        long rendered = 0, calls = 0;
        double area = 0;
        var perFrame = Math.Max(1, mouseHz / 60);
        foreach (var drag in EraserDrags(mouseHz))
        {
            var clock = new Clock();
            var session = new EraseSession(new EraseGesture(document.Items, EraserMode.Precision), drag[0], radius, clock);
            session.FrameReady += f =>
            {
                rendered += f.Added.Count;
                calls++;
                area += f.Dirty.IsEmpty ? 0 : f.Dirty.Width * f.Dirty.Height;
            };
            session.Start();
            for (var i = 1; i < drag.Count; i++)
            {
                session.MoveTo(drag[i]);
                if (i % perFrame == 0)
                {
                    clock.Tick();
                }
            }

            session.Finish(document, commit: true);
        }

        return (rendered, area, calls);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(1000)]
    public void TheNewPreviewDoesLessRedrawWork(int mouseHz)
    {
        const double radius = 24 / 2.0 * 1.33; // default 24 DIP eraser at 133 % display scaling
        var before = Old(mouseHz, radius);
        var after = New(mouseHz, radius);
        output.WriteLine($"{mouseHz} Hz mouse: preview updates {before.Calls} -> {after.Calls}, pieces drawn {before.Rendered} -> {after.Rendered}, " +
            $"redrawn area {before.Area / 1e6:F1} -> {after.Area / 1e6:F1} Mpx");

        Assert.True(after.Rendered < before.Rendered);
        Assert.True(after.Area < before.Area);
        Assert.True(after.Calls <= before.Calls);
    }
}
