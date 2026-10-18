using System.Windows;

namespace VIZEN.App.Models;

public enum DrawTool
{
    Pen,
    Arrow,
    Rectangle,
    Ellipse,
    Text,
    Eraser,
}

public enum AnnotationKind
{
    Pen,
    Arrow,
    Rectangle,
    Ellipse,
    Text,
}

/// <summary>
/// Precision: cuts freehand strokes exactly where the eraser passes (shapes and text are removed whole).
/// Object: removes every annotation the eraser touches, whole.
/// </summary>
public enum EraserMode
{
    Precision,
    Object,
}

public enum ArrowHead
{
    Filled,
    Open,
}

/// <summary>
/// One Smart Draw annotation, as vector data. Coordinates are physical pixels relative to the top-left corner of the
/// monitor it was drawn on (<see cref="Monitor"/>), so drawings survive DPI changes and monitor rearrangement and can
/// be scaled to another resolution. Persisted in drawings.json when the user saves a drawing.
/// </summary>
public sealed class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AnnotationKind Kind { get; set; }

    /// <summary>Device name of the monitor (e.g. \\.\DISPLAY1).</summary>
    public string Monitor { get; set; } = string.Empty;

    /// <summary>Pen: points as x0, y0, x1, y1, … (flat, compact in memory and on disk).</summary>
    public List<double> Points { get; set; } = [];

    /// <summary>Arrow: start → end. Rectangle / ellipse: the two corners of the drag (any direction). Text: X1, Y1 = top-left.</summary>
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; }

    /// <summary>Text: measured size in pixels (used for hit testing by the eraser and the text tool).</summary>
    public double TextWidth { get; set; }
    public double TextHeight { get; set; }

    /// <summary>#AARRGGBB.</summary>
    public string Color { get; set; } = "#FFFF354E";

    /// <summary>Line thickness in pixels.</summary>
    public double Thickness { get; set; } = 4;

    public ArrowHead Head { get; set; }

    public Annotation Clone() => new()
    {
        Id = Id, Kind = Kind, Monitor = Monitor, Points = [.. Points], X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2, Text = Text,
        FontSize = FontSize, TextWidth = TextWidth, TextHeight = TextHeight, Color = Color, Thickness = Thickness, Head = Head,
    };
}

/// <summary>A monitor as Smart Draw sees it: bounds and work area in physical pixels, and its DPI scale.</summary>
public sealed record MonitorInfo(string Key, int Left, int Top, int Width, int Height, double DpiScale, bool IsPrimary)
{
    public int WorkLeft { get; init; } = Left;
    public int WorkTop { get; init; } = Top;
    public int WorkWidth { get; init; } = Width;
    public int WorkHeight { get; init; } = Height;
}

/// <summary>Pure geometry of the drawing tools (pixels).</summary>
public static class DrawingGeometry
{
    /// <summary>Pen points closer than this to the previous one are not stored (keeps strokes small).</summary>
    public const double MinPointDistance = 1.5;

    /// <summary>The rectangle spanned by a drag, whatever its direction.</summary>
    public static Rect Normalize(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    /// <summary>Bounds of an ellipse dragged from <paramref name="a"/> to <paramref name="b"/>; a circle when <paramref name="circle"/>.</summary>
    public static Rect EllipseBounds(Point a, Point b, bool circle)
    {
        if (!circle)
        {
            return Normalize(a, b);
        }

        var side = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        var end = new Point(a.X + (b.X >= a.X ? side : -side), a.Y + (b.Y >= a.Y ? side : -side));
        return Normalize(a, end);
    }

    /// <summary>Adds a pen point unless it is closer than <see cref="MinPointDistance"/> to the last one.</summary>
    public static bool AddPenPoint(List<double> points, Point p)
    {
        var n = points.Count;
        if (n >= 2 && Distance(new Point(points[n - 2], points[n - 1]), p) < MinPointDistance)
        {
            return false;
        }

        points.Add(p.X);
        points.Add(p.Y);
        return true;
    }

    /// <summary>The two back corners of an arrowhead at <paramref name="end"/>.</summary>
    public static (Point Left, Point Right) ArrowWings(Point start, Point end, double thickness)
    {
        var length = Math.Max(12, thickness * 4);
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        const double spread = 28 * Math.PI / 180;
        return (new Point(end.X - length * Math.Cos(angle - spread), end.Y - length * Math.Sin(angle - spread)),
                new Point(end.X - length * Math.Cos(angle + spread), end.Y - length * Math.Sin(angle + spread)));
    }

    /// <summary>True when the annotation is big enough to keep (a click without a drag draws nothing).</summary>
    public static bool IsMeaningful(Annotation a) => a.Kind switch
    {
        AnnotationKind.Pen => a.Points.Count >= 4,
        AnnotationKind.Arrow => Distance(new Point(a.X1, a.Y1), new Point(a.X2, a.Y2)) >= 6,
        AnnotationKind.Rectangle or AnnotationKind.Ellipse => Math.Abs(a.X2 - a.X1) >= 3 && Math.Abs(a.Y2 - a.Y1) >= 3,
        AnnotationKind.Text => a.Text.Trim().Length > 0,
        _ => false,
    };

    /// <summary>Whether <paramref name="p"/> is on the annotation (within <paramref name="tolerance"/> pixels of its line).</summary>
    public static bool HitTest(Annotation a, Point p, double tolerance)
    {
        var reach = tolerance + a.Thickness / 2;
        switch (a.Kind)
        {
            case AnnotationKind.Pen:
                if (a.Points.Count == 2)
                {
                    return Distance(new Point(a.Points[0], a.Points[1]), p) <= reach;
                }

                for (var i = 0; i + 3 < a.Points.Count; i += 2)
                {
                    if (DistanceToSegment(p, new Point(a.Points[i], a.Points[i + 1]), new Point(a.Points[i + 2], a.Points[i + 3])) <= reach)
                    {
                        return true;
                    }
                }

                return false;

            case AnnotationKind.Arrow:
            {
                var start = new Point(a.X1, a.Y1);
                var end = new Point(a.X2, a.Y2);
                if (DistanceToSegment(p, start, end) <= reach)
                {
                    return true;
                }

                var (left, right) = ArrowWings(start, end, a.Thickness);
                return DistanceToSegment(p, end, left) <= reach || DistanceToSegment(p, end, right) <= reach;
            }

            case AnnotationKind.Rectangle:
            {
                var r = Normalize(new Point(a.X1, a.Y1), new Point(a.X2, a.Y2));
                var outer = r;
                outer.Inflate(reach, reach);
                var inner = r;
                inner.Inflate(-reach, -reach);
                return outer.Contains(p) && (inner.IsEmpty || inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p));
            }

            case AnnotationKind.Ellipse:
            {
                var r = Normalize(new Point(a.X1, a.Y1), new Point(a.X2, a.Y2));
                var rx = r.Width / 2;
                var ry = r.Height / 2;
                if (rx <= 0 || ry <= 0)
                {
                    return false;
                }

                var nx = (p.X - (r.X + rx)) / rx;
                var ny = (p.Y - (r.Y + ry)) / ry;
                return Math.Abs(Math.Sqrt(nx * nx + ny * ny) - 1) * Math.Min(rx, ry) <= reach;
            }

            case AnnotationKind.Text:
            {
                var r = new Rect(a.X1, a.Y1, Math.Max(1, a.TextWidth), Math.Max(1, a.TextHeight));
                r.Inflate(tolerance, tolerance);
                return r.Contains(p);
            }
        }

        return false;
    }

    public static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
        {
            return Distance(p, a);
        }

        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return Distance(p, new Point(a.X + t * dx, a.Y + t * dy));
    }
}

/// <summary>Maps saved drawings onto the monitors connected now.</summary>
public static class MonitorMapping
{
    /// <summary>The monitor with this key, or the primary one (or the first) when it is no longer connected.</summary>
    public static MonitorInfo? Resolve(string key, IReadOnlyList<MonitorInfo> current) =>
        current.FirstOrDefault(m => m.Key == key) ?? current.FirstOrDefault(m => m.IsPrimary) ?? current.FirstOrDefault();

    /// <summary>
    /// Moves a saved annotation from the monitor it was saved on (<paramref name="saved"/>, null when unknown) to
    /// <paramref name="target"/>: positions scale with the resolution, line widths and font sizes with the smaller factor.
    /// </summary>
    public static Annotation Map(Annotation a, MonitorInfo? saved, MonitorInfo target)
    {
        var copy = a.Clone();
        copy.Monitor = target.Key;
        if (saved is null || saved.Width <= 0 || saved.Height <= 0 || (saved.Width == target.Width && saved.Height == target.Height))
        {
            return copy;
        }

        var sx = (double)target.Width / saved.Width;
        var sy = (double)target.Height / saved.Height;
        for (var i = 0; i + 1 < copy.Points.Count; i += 2)
        {
            copy.Points[i] *= sx;
            copy.Points[i + 1] *= sy;
        }

        (copy.X1, copy.Y1, copy.X2, copy.Y2) = (copy.X1 * sx, copy.Y1 * sy, copy.X2 * sx, copy.Y2 * sy);
        var s = Math.Min(sx, sy);
        copy.Thickness *= s;
        copy.FontSize *= s;
        copy.TextWidth *= s;
        copy.TextHeight *= s;
        return copy;
    }
}
