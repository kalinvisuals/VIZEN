using System.Windows;

namespace VIZEN.App.Models;

/// <summary>
/// Geometry for crop regions. Regions are normalized rectangles: (0,0)-(1,1) is the whole source
/// window, so they stay correct when the source is resized.
/// </summary>
public static class RegionMath
{
    public static readonly Rect Full = new(0, 0, 1, 1);

    /// <summary>Smallest region edge, in source pixels.</summary>
    public const double MinEdgePixels = 16;

    public static bool IsFull(Rect r) =>
        r.X <= 0.0001 && r.Y <= 0.0001 && r.Width >= 0.9999 && r.Height >= 0.9999;

    /// <summary>Keeps <paramref name="r"/> inside the unit square without changing its size (when possible).</summary>
    public static Rect ClampInside(Rect r)
    {
        var w = Math.Min(r.Width, 1);
        var h = Math.Min(r.Height, 1);
        var x = Math.Clamp(r.X, 0, 1 - w);
        var y = Math.Clamp(r.Y, 0, 1 - h);
        return new Rect(x, y, w, h);
    }

    /// <summary>Rectangle spanning two points, clipped to the unit square.</summary>
    public static Rect FromPoints(Point a, Point b)
    {
        var x1 = Math.Clamp(Math.Min(a.X, b.X), 0, 1);
        var y1 = Math.Clamp(Math.Min(a.Y, b.Y), 0, 1);
        var x2 = Math.Clamp(Math.Max(a.X, b.X), 0, 1);
        var y2 = Math.Clamp(Math.Max(a.Y, b.Y), 0, 1);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>
    /// Converts a pixel aspect ratio (width / height, as the user sees it) into the equivalent ratio of
    /// normalized width to normalized height for a source of the given pixel size.
    /// </summary>
    public static double NormalizedAspect(double pixelAspect, int sourceWidth, int sourceHeight) =>
        sourceWidth > 0 && sourceHeight > 0 ? pixelAspect * sourceHeight / sourceWidth : pixelAspect;

    /// <summary>
    /// Reshapes <paramref name="r"/> to the normalized aspect <paramref name="normAspect"/> (w/h), keeping its
    /// centre and area as far as the unit square allows.
    /// </summary>
    public static Rect WithAspect(Rect r, double normAspect)
    {
        var area = Math.Max(r.Width * r.Height, 0.0004);
        var w = Math.Sqrt(area * normAspect);
        var h = w / normAspect;
        var scale = Math.Min(1, Math.Min(1 / w, 1 / h));
        w *= scale;
        h *= scale;
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        return ClampInside(new Rect(cx - w / 2, cy - h / 2, w, h));
    }

    /// <summary>Enforces a minimum size (normalized) while keeping the rectangle anchored at its top-left.</summary>
    public static Rect WithMinimumSize(Rect r, double minW, double minH) =>
        ClampInside(new Rect(r.X, r.Y, Math.Max(r.Width, minW), Math.Max(r.Height, minH)));

    /// <summary>Default region for "Add region": centred, 40% of each side.</summary>
    public static Rect DefaultRegion(int index)
    {
        var offset = (index % 5) * 0.04;
        return ClampInside(new Rect(0.3 + offset, 0.3 + offset, 0.4, 0.4));
    }
}
