using System.Windows;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Opens OBS output windows. New outputs open at 1:1 source pixels (best capture quality), capped to the
/// work area, cascading from the top-left; restored outputs go back to their saved bounds.
/// </summary>
public sealed class OutputPresenter : IOutputPresenter
{
    private const double EdgeMargin = 40;
    private const double Stagger = 36;

    public void Show(OutputViewModel output, int index)
    {
        var window = new OutputWindow(output);
        var work = SystemParameters.WorkArea;

        if (output.InitialBounds is { } saved && Rect.Intersect(saved, VirtualDesktop()).Width >= 80)
        {
            window.Left = saved.Left;
            window.Top = saved.Top;
            window.Width = saved.Width;
            window.Height = saved.Height;
            window.Show();
            return;
        }

        var offset = (index % 8) * Stagger;
        window.Left = work.Left + EdgeMargin + offset;
        window.Top = work.Top + EdgeMargin + offset;
        window.Show();

        // Size after showing so the window's DPI is known. Before the first frame, keep the 16:9 default.
        if (output.ContentPixelSize is { } pixels)
        {
            var maxWidthPx = work.Width * 0.85 * window.GetDpiScaleX();
            var maxHeightPx = work.Height * 0.85 * window.GetDpiScaleY();
            var scale = Math.Min(1, Math.Min(maxWidthPx / pixels.Width, maxHeightPx / pixels.Height));
            window.SetPixelSize(new Size(pixels.Width * scale, pixels.Height * scale));
        }
    }

    private static Rect VirtualDesktop() => new(
        SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
}

internal static class WindowDpiExtensions
{
    public static double GetDpiScaleX(this Window window) => System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;

    public static double GetDpiScaleY(this Window window) => System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleY;
}
