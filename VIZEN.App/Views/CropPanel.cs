using System.Windows;
using System.Windows.Controls;
using VIZEN.App.Models;

namespace VIZEN.App.Views;

/// <summary>
/// Shows only the <see cref="Crop"/> part (normalized 0..1) of its child, scaled to fill the panel.
/// Works for any element (image, animated GIF, video), unlike a brush viewbox.
/// </summary>
public sealed class CropPanel : Panel
{
    public static readonly DependencyProperty CropProperty = DependencyProperty.Register(
        nameof(Crop), typeof(Rect), typeof(CropPanel),
        new FrameworkPropertyMetadata(RegionMath.Full, FrameworkPropertyMetadataOptions.AffectsArrange));

    public CropPanel()
    {
        ClipToBounds = true;
    }

    public Rect Crop
    {
        get => (Rect)GetValue(CropProperty);
        set => SetValue(CropProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var crop = Crop.Width > 0 && Crop.Height > 0 ? Crop : RegionMath.Full;
        var width = finalSize.Width / crop.Width;
        var height = finalSize.Height / crop.Height;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(-crop.X * width, -crop.Y * height, width, height));
        }

        return finalSize;
    }
}
