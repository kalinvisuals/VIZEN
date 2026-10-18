using System.Windows;

namespace VIZEN.App.Views;

/// <summary>Restoring saved window positions (device-independent units) safely.</summary>
internal static class ScreenPlacement
{
    /// <summary>Smallest part of a window that must be on the desktop for its saved position to be used.</summary>
    public const double MinVisible = 24;

    /// <summary>
    /// The saved bounds when enough of them is still on the desktop, otherwise null (the caller picks a default):
    /// a monitor may have been disconnected or rearranged since the window was placed.
    /// </summary>
    public static Rect? Restorable(Rect? saved)
    {
        if (saved is not { Width: > 0, Height: > 0 } bounds)
        {
            return null;
        }

        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(desktop, bounds);
        return !visible.IsEmpty && visible.Width >= MinVisible && visible.Height >= MinVisible ? bounds : null;
    }
}
