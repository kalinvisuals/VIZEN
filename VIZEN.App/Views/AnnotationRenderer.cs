using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VIZEN.App.Models;

namespace VIZEN.App.Views;

/// <summary>
/// Draws Smart Draw annotations with WPF vector drawing, in physical-pixel coordinates (the canvas scales them to its
/// DPI). Brushes and pens are frozen and cached. Each annotation is drawn once into its own visual; only the
/// annotation being drawn is redrawn while the mouse moves.
/// </summary>
internal static class AnnotationRenderer
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];
    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static void Render(DrawingContext dc, Annotation a)
    {
        var brush = BrushFor(a.Color);
        switch (a.Kind)
        {
            case AnnotationKind.Pen:
                RenderPen(dc, a, brush);
                break;
            case AnnotationKind.Arrow:
                RenderArrow(dc, a, brush);
                break;
            case AnnotationKind.Rectangle:
                dc.DrawRectangle(null, PenFor(brush, a.Thickness, PenLineJoin.Miter), DrawingGeometry.Normalize(new Point(a.X1, a.Y1), new Point(a.X2, a.Y2)));
                break;
            case AnnotationKind.Ellipse:
            {
                var r = DrawingGeometry.Normalize(new Point(a.X1, a.Y1), new Point(a.X2, a.Y2));
                dc.DrawEllipse(null, PenFor(brush, a.Thickness, PenLineJoin.Round), new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            }
            case AnnotationKind.Text:
                RenderText(dc, a, brush);
                break;
        }
    }

    /// <summary>Size of a text annotation in pixels (for its bounds).</summary>
    public static Size MeasureText(string text, double fontSize)
    {
        var ft = Format(text.Length == 0 ? " " : text, fontSize, BrushFor("#FFFFFFFF"));
        return new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
    }

    private static void RenderPen(DrawingContext dc, Annotation a, Brush brush)
    {
        var points = a.Points;
        if (points.Count < 2)
        {
            return;
        }

        if (points.Count == 2)
        {
            dc.DrawEllipse(brush, null, new Point(points[0], points[1]), a.Thickness / 2, a.Thickness / 2);
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0], points[1]), false, false);
            var rest = new Point[points.Count / 2 - 1];
            for (var i = 0; i < rest.Length; i++)
            {
                rest[i] = new Point(points[2 + i * 2], points[3 + i * 2]);
            }

            ctx.PolyLineTo(rest, true, true);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, PenFor(brush, a.Thickness, PenLineJoin.Round), geometry);
    }

    private static void RenderArrow(DrawingContext dc, Annotation a, Brush brush)
    {
        var start = new Point(a.X1, a.Y1);
        var end = new Point(a.X2, a.Y2);
        var (left, right) = DrawingGeometry.ArrowWings(start, end, a.Thickness);
        var pen = PenFor(brush, a.Thickness, PenLineJoin.Round);
        if (a.Head == ArrowHead.Filled)
        {
            // The shaft stops inside the head so its round cap does not poke out of the tip.
            var baseMid = new Point((left.X + right.X) / 2, (left.Y + right.Y) / 2);
            dc.DrawLine(pen, start, baseMid);
            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(end, true, true);
                ctx.PolyLineTo([left, right], true, true);
            }

            head.Freeze();
            dc.DrawGeometry(brush, PenFor(brush, Math.Max(1, a.Thickness / 2), PenLineJoin.Round), head);
        }
        else
        {
            dc.DrawLine(pen, start, end);
            dc.DrawLine(pen, end, left);
            dc.DrawLine(pen, end, right);
        }
    }

    private static void RenderText(DrawingContext dc, Annotation a, SolidColorBrush brush)
    {
        if (a.Text.Length == 0)
        {
            return;
        }

        var geometry = Format(a.Text, a.FontSize, brush).BuildGeometry(new Point(a.X1, a.Y1));
        geometry.Freeze();

        // A thin contrasting outline keeps the text readable on any background (games, white pages).
        var c = brush.Color;
        var light = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 140;
        var outline = BrushFor(light ? "#B0000000" : "#B0FFFFFF");
        dc.DrawGeometry(null, PenFor(outline, Math.Max(2, a.FontSize / 9), PenLineJoin.Round), geometry);
        dc.DrawGeometry(brush, null, geometry);
    }

    private static FormattedText Format(string text, double fontSize, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, Math.Max(1, fontSize), brush, 1.0);

    private static SolidColorBrush BrushFor(string hex)
    {
        if (!Brushes.TryGetValue(hex, out var brush))
        {
            Color color;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(hex);
            }
            catch (FormatException)
            {
                color = Colors.Red;
            }

            brush = new SolidColorBrush(color);
            brush.Freeze();
            Brushes[hex] = brush;
        }

        return brush;
    }

    private static Pen PenFor(Brush brush, double thickness, PenLineJoin join)
    {
        var pen = new Pen(brush, Math.Max(0.5, thickness)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = join };
        pen.Freeze();
        return pen;
    }
}
