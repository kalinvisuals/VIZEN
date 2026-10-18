using System.Windows;

namespace VIZEN.App.Models;

/// <summary>
/// One continuous eraser drag (mouse down → up). The eraser is a circle; between two mouse positions it sweeps a
/// capsule, so fast movements leave no gaps. Precision mode cuts freehand strokes exactly where the capsule covers
/// them (the remaining pieces keep their colour, thickness and points, and their round ends just touch the eraser's
/// edge); shapes and text are removed whole. Object mode removes everything it touches, whole. Nothing changes in the
/// document until <see cref="Commit"/>, which records the whole drag as one undo step.
/// </summary>
public sealed class EraseGesture
{
    private readonly List<Annotation> _candidates;
    private readonly Dictionary<Annotation, List<Annotation>> _pieces = [];
    private readonly HashSet<Annotation> _whole = [];
    private readonly Dictionary<Annotation, Rect> _bounds = [];
    private readonly List<Annotation> _frameRemoved = [];
    private readonly List<Annotation> _frameAdded = [];
    private readonly List<Annotation> _frameWhole = [];

    public EraseGesture(IEnumerable<Annotation> annotations, EraserMode mode)
    {
        _candidates = annotations.ToList();
        Mode = mode;
    }

    public EraserMode Mode { get; }

    /// <summary>Freehand strokes that were cut (hidden while the drag lasts; replaced by <see cref="Fragments"/>).</summary>
    public IEnumerable<Annotation> CutStrokes => _pieces.Keys;

    /// <summary>What is left of the cut strokes so far.</summary>
    public IEnumerable<Annotation> Fragments => _pieces.Values.SelectMany(p => p);

    /// <summary>Annotations that will be removed whole.</summary>
    public IReadOnlyCollection<Annotation> WholeRemoved => _whole;

    public bool HasChanges => _pieces.Count > 0 || _whole.Count > 0;

    /// <summary>
    /// What changed since the last call, for a localized redraw: pieces that went out (an original stroke the first
    /// time it is cut, later only the piece that was cut again), pieces that came in, objects to remove whole, and the
    /// region they cover (old and new geometry, line widths included). A piece created and cut again within the same
    /// frame appears in neither list.
    /// </summary>
    public EraseFrame TakeFrame()
    {
        var transient = _frameAdded.Intersect(_frameRemoved).ToHashSet();
        var removed = _frameRemoved.Where(a => !transient.Contains(a)).ToList();
        var added = _frameAdded.Where(a => !transient.Contains(a)).ToList();
        var whole = _frameWhole.ToList();
        var dirty = Rect.Empty;
        foreach (var a in removed.Concat(added).Concat(whole))
        {
            dirty.Union(EraserGeometry.AnnotationBounds(a));
        }

        _frameRemoved.Clear();
        _frameAdded.Clear();
        _frameWhole.Clear();
        return new EraseFrame(removed, added, whole, dirty);
    }

    /// <summary>Erases along the move from <paramref name="from"/> to <paramref name="to"/> (pixels). True when something changed.</summary>
    public bool Apply(Point from, Point to, double radius)
    {
        var sweep = DrawingGeometry.Normalize(from, to);
        sweep.Inflate(radius, radius);
        var changed = false;
        foreach (var annotation in _candidates)
        {
            if (_whole.Contains(annotation))
            {
                continue;
            }

            if (annotation.Kind == AnnotationKind.Pen && Mode == EraserMode.Precision)
            {
                changed |= Cut(annotation, from, to, radius, sweep);
            }
            else if (Touches(annotation, from, to, radius, sweep))
            {
                _whole.Add(annotation);
                _frameWhole.Add(annotation);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Applies the drag to the document as one undo step. False when nothing was erased.</summary>
    public bool Commit(DrawingDocument document)
    {
        if (!HasChanges)
        {
            return false;
        }

        var replacements = new Dictionary<Annotation, IReadOnlyList<Annotation>>();
        foreach (var (stroke, pieces) in _pieces)
        {
            replacements[stroke] = pieces;
        }

        foreach (var annotation in _whole)
        {
            replacements[annotation] = [];
        }

        document.ReplaceParts(replacements);
        return true;
    }

    private bool Cut(Annotation stroke, Point from, Point to, double radius, Rect sweep)
    {
        var current = _pieces.TryGetValue(stroke, out var existing) ? existing : [stroke];
        List<Annotation>? next = null;
        for (var i = 0; i < current.Count; i++)
        {
            var piece = current[i];
            var reach = radius + piece.Thickness / 2;
            var area = sweep;
            area.Inflate(piece.Thickness / 2, piece.Thickness / 2);
            var runs = BoundsOf(piece).IntersectsWith(area) ? EraserGeometry.ErasePolyline(piece.Points, from, to, reach) : null;
            if (runs is null)
            {
                next?.Add(piece);
                continue;
            }

            next ??= [.. current.Take(i)];
            _frameRemoved.Add(piece);
            foreach (var run in runs)
            {
                var fragment = piece.Clone();
                fragment.Id = Guid.NewGuid();
                fragment.Points = run;
                next.Add(fragment);
                _frameAdded.Add(fragment);
            }
        }

        if (next is null)
        {
            return false;
        }

        _pieces[stroke] = next;
        return true;
    }

    private bool Touches(Annotation a, Point from, Point to, double radius, Rect sweep)
    {
        if (a.Kind == AnnotationKind.Pen)
        {
            var area = sweep;
            area.Inflate(a.Thickness / 2, a.Thickness / 2);
            return BoundsOf(a).IntersectsWith(area) && EraserGeometry.PolylineDistance(a.Points, from, to) <= radius + a.Thickness / 2;
        }

        // Shapes and text: their outline (or text box) within the eraser anywhere along the move.
        var length = DrawingGeometry.Distance(from, to);
        var steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(1, radius / 2)));
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            if (DrawingGeometry.HitTest(a, new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t), radius))
            {
                return true;
            }
        }

        return false;
    }

    private Rect BoundsOf(Annotation a)
    {
        if (!_bounds.TryGetValue(a, out var r))
        {
            r = EraserGeometry.PolylineBounds(a.Points, a.Thickness / 2);
            _bounds[a] = r;
        }

        return r;
    }
}

/// <summary>One frame's worth of eraser changes (see <see cref="EraseGesture.TakeFrame"/>).</summary>
public sealed record EraseFrame(IReadOnlyList<Annotation> Removed, IReadOnlyList<Annotation> Added, IReadOnlyList<Annotation> WholeRemoved, Rect Dirty)
{
    public bool IsEmpty => Removed.Count == 0 && Added.Count == 0 && WholeRemoved.Count == 0;
}

/// <summary>Geometry of the precision eraser (pixels).</summary>
public static class EraserGeometry
{
    /// <summary>Area an annotation covers on screen (line width included), in pixels.</summary>
    public static Rect AnnotationBounds(Annotation a)
    {
        var half = a.Thickness / 2 + 1;
        switch (a.Kind)
        {
            case AnnotationKind.Pen:
                return PolylineBounds(a.Points, half);
            case AnnotationKind.Text:
                return new Rect(a.X1, a.Y1, Math.Max(1, a.TextWidth), Math.Max(1, a.TextHeight));
            case AnnotationKind.Arrow:
            {
                var r = DrawingGeometry.Normalize(new Point(a.X1, a.Y1), new Point(a.X2, a.Y2));
                var (left, right) = DrawingGeometry.ArrowWings(new Point(a.X1, a.Y1), new Point(a.X2, a.Y2), a.Thickness);
                r.Union(left);
                r.Union(right);
                r.Inflate(half, half);
                return r;
            }

            default:
            {
                var r = DrawingGeometry.Normalize(new Point(a.X1, a.Y1), new Point(a.X2, a.Y2));
                r.Inflate(half, half);
                return r;
            }
        }
    }

    /// <summary>Pieces shorter than this are dropped (they would show as stray dots).</summary>
    public const double MinPieceLength = 0.5;

    /// <summary>
    /// Removes from a polyline (flat x, y list) every part within <paramref name="reach"/> of the segment
    /// <paramref name="e0"/>–<paramref name="e1"/>. Null when nothing is within reach; otherwise the remaining runs
    /// (possibly none), each starting / ending exactly at the edge of the erased area.
    /// </summary>
    public static List<List<double>>? ErasePolyline(IReadOnlyList<double> flat, Point e0, Point e1, double reach)
    {
        var n = flat.Count / 2;
        if (n == 0)
        {
            return null;
        }

        if (n == 1)
        {
            return DrawingGeometry.DistanceToSegment(new Point(flat[0], flat[1]), e0, e1) <= reach ? [] : null;
        }

        if (PolylineDistance(flat, e0, e1) > reach)
        {
            return null;
        }

        bool Inside(Point p) => DrawingGeometry.DistanceToSegment(p, e0, e1) <= reach;

        var runs = new List<List<Point>>();
        List<Point>? current = null;
        var first = new Point(flat[0], flat[1]);
        if (!Inside(first))
        {
            current = [first];
        }

        var step = Math.Max(0.25, reach / 4);
        for (var i = 0; i + 1 < n; i++)
        {
            var a = new Point(flat[i * 2], flat[i * 2 + 1]);
            var b = new Point(flat[i * 2 + 2], flat[i * 2 + 3]);
            Point At(double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

            // Samples closer together than the erased band is wide, plus the point nearest to the eraser's path, so
            // even a segment that only grazes the eraser is cut where it should be.
            var length = DrawingGeometry.Distance(a, b);
            var count = Math.Max(1, (int)Math.Ceiling(length / step));
            var samples = new List<double>(count + 2);
            for (var k = 0; k <= count; k++)
            {
                samples.Add((double)k / count);
            }

            samples.Add(ClosestParameter(a, b, e0, e1));
            samples.Sort();

            var previous = samples[0];
            var previousInside = Inside(At(previous));
            foreach (var t in samples.Skip(1))
            {
                if (t <= previous)
                {
                    continue;
                }

                var inside = Inside(At(t));
                if (inside != previousInside)
                {
                    // Boundary by bisection; keep the point on the outer side so the remaining end touches the edge.
                    double lo = previous, hi = t;
                    for (var iteration = 0; iteration < 14; iteration++)
                    {
                        var mid = (lo + hi) / 2;
                        if (Inside(At(mid)) == previousInside)
                        {
                            lo = mid;
                        }
                        else
                        {
                            hi = mid;
                        }
                    }

                    if (inside)
                    {
                        current?.Add(At(lo));
                        if (current is not null)
                        {
                            runs.Add(current);
                        }

                        current = null;
                    }
                    else
                    {
                        current = [At(hi)];
                    }
                }

                previous = t;
                previousInside = inside;
            }

            if (!previousInside)
            {
                current ??= [b];
                if (current[^1] != b)
                {
                    current.Add(b);
                }
            }
        }

        if (current is not null)
        {
            runs.Add(current);
        }

        return runs.Select(Clean).Where(r => r is not null).Select(r => r!).ToList();
    }

    /// <summary>Smallest distance between a polyline (flat x, y list) and a segment.</summary>
    public static double PolylineDistance(IReadOnlyList<double> flat, Point e0, Point e1)
    {
        var n = flat.Count / 2;
        if (n == 1)
        {
            return DrawingGeometry.DistanceToSegment(new Point(flat[0], flat[1]), e0, e1);
        }

        var best = double.MaxValue;
        for (var i = 0; i + 1 < n; i++)
        {
            best = Math.Min(best, SegmentDistance(new Point(flat[i * 2], flat[i * 2 + 1]), new Point(flat[i * 2 + 2], flat[i * 2 + 3]), e0, e1));
        }

        return best;
    }

    public static Rect PolylineBounds(IReadOnlyList<double> flat, double margin)
    {
        if (flat.Count < 2)
        {
            return Rect.Empty;
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i + 1 < flat.Count; i += 2)
        {
            minX = Math.Min(minX, flat[i]);
            maxX = Math.Max(maxX, flat[i]);
            minY = Math.Min(minY, flat[i + 1]);
            maxY = Math.Max(maxY, flat[i + 1]);
        }

        return new Rect(minX - margin, minY - margin, maxX - minX + 2 * margin, maxY - minY + 2 * margin);
    }

    /// <summary>Distance between segments p1–q1 and p2–q2.</summary>
    public static double SegmentDistance(Point p1, Point q1, Point p2, Point q2)
    {
        var (s, t) = Closest(p1, q1, p2, q2);
        var c1 = new Point(p1.X + (q1.X - p1.X) * s, p1.Y + (q1.Y - p1.Y) * s);
        var c2 = new Point(p2.X + (q2.X - p2.X) * t, p2.Y + (q2.Y - p2.Y) * t);
        return DrawingGeometry.Distance(c1, c2);
    }

    /// <summary>Parameter (0..1) of the point of a–b nearest to segment e0–e1.</summary>
    public static double ClosestParameter(Point a, Point b, Point e0, Point e1) => Closest(a, b, e0, e1).S;

    /// <summary>Closest points of two segments (Ericson, Real-Time Collision Detection 5.1.9): parameters on each.</summary>
    private static (double S, double T) Closest(Point p1, Point q1, Point p2, Point q2)
    {
        const double eps = 1e-12;
        double d1x = q1.X - p1.X, d1y = q1.Y - p1.Y, d2x = q2.X - p2.X, d2y = q2.Y - p2.Y, rx = p1.X - p2.X, ry = p1.Y - p2.Y;
        double a = d1x * d1x + d1y * d1y, e = d2x * d2x + d2y * d2y, f = d2x * rx + d2y * ry;
        if (a <= eps && e <= eps)
        {
            return (0, 0);
        }

        if (a <= eps)
        {
            return (0, Math.Clamp(f / e, 0, 1));
        }

        var c = d1x * rx + d1y * ry;
        if (e <= eps)
        {
            return (Math.Clamp(-c / a, 0, 1), 0);
        }

        var b = d1x * d2x + d1y * d2y;
        var denom = a * e - b * b;
        var s = denom > eps ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
        var t = (b * s + f) / e;
        if (t < 0)
        {
            t = 0;
            s = Math.Clamp(-c / a, 0, 1);
        }
        else if (t > 1)
        {
            t = 1;
            s = Math.Clamp((b - c) / a, 0, 1);
        }

        return (s, t);
    }

    private static List<double>? Clean(List<Point> run)
    {
        var points = new List<Point>(run.Count);
        foreach (var p in run)
        {
            if (points.Count == 0 || DrawingGeometry.Distance(points[^1], p) > 1e-6)
            {
                points.Add(p);
            }
        }

        var length = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            length += DrawingGeometry.Distance(points[i - 1], points[i]);
        }

        if (points.Count < 2 || length < MinPieceLength)
        {
            return null;
        }

        var flat = new List<double>(points.Count * 2);
        foreach (var p in points)
        {
            flat.Add(p.X);
            flat.Add(p.Y);
        }

        return flat;
    }
}
