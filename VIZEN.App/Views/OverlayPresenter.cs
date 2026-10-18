using System.Windows;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Opens overlay windows. Restored overlays go back to their saved bounds when those are still on
/// screen; new ones open near the bottom-right of the primary work area (clear of VIZEN's own caption
/// buttons), staggered so they don't stack exactly. Overlays are deliberately not owned by the main
/// window, so minimizing VIZEN leaves them on screen.
/// </summary>
public sealed class OverlayPresenter : IOverlayPresenter
{
    private const double DefaultWidth = 480;
    private const double EdgeMargin = 24;
    private const double Stagger = 32;
    private const double MinVisiblePart = 80;

    public void Show(OverlayViewModel overlay, int index)
    {
        var window = new OverlayWindow(overlay);

        if (overlay.InitialBounds is { } saved && IsReachable(saved))
        {
            window.Left = saved.Left;
            window.Top = saved.Top;
            window.Width = Math.Max(window.MinWidth, saved.Width);
            window.Height = Math.Max(window.MinHeight, saved.Height);
        }
        else
        {
            PlaceDefault(window, overlay, index);
        }

        window.Show();
    }

    private static void PlaceDefault(Window window, OverlayViewModel overlay, int index)
    {
        // Prefer the real frame (captures are shared, so one may already be running). Otherwise use the
        // reported window size; minimized windows report a tiny caption-sized area, so fall back to 16:9
        // and let the window fit itself when the first frame arrives.
        var source = overlay.Capture.SourceSize;
        var aspect = overlay.ContentAspect is { } contentAspect
            ? 1 / contentAspect
            : source.Width >= 200 && source.Height >= 100
                ? source.Height * overlay.Viewbox.Height / (source.Width * overlay.Viewbox.Width)
                : 9.0 / 16.0;
        var work = SystemParameters.WorkArea;

        window.Width = Math.Min(DefaultWidth, work.Width / 2);
        window.Height = Math.Clamp(window.Width * aspect, window.MinHeight, work.Height * 0.8);

        var offset = (index % 8) * Stagger;
        window.Left = work.Right - window.Width - EdgeMargin - offset;
        window.Top = work.Bottom - window.Height - EdgeMargin - offset;
    }

    /// <summary>
    /// True if enough of <paramref name="bounds"/> lies on the virtual desktop to grab it — a saved
    /// position can be off-screen if a monitor was disconnected since the profile was saved.
    /// </summary>
    private static bool IsReachable(Rect bounds)
    {
        var desktop = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(desktop, bounds);
        return !visible.IsEmpty && visible.Width >= MinVisiblePart && visible.Height >= MinVisiblePart / 2;
    }
}
