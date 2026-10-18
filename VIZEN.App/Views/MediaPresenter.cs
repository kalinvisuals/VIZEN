using System.Windows;
using Microsoft.Win32;
using VIZEN.App.Interop;
using VIZEN.App.Media;
using VIZEN.App.Models;
using VIZEN.App.Services;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Owns one <see cref="MediaLayerWindow"/> per shown layer. Windows are kept while hidden (so scheduled
/// logos reappear instantly) and recreated when the layer switches between overlay and OBS output.
/// </summary>
public sealed class MediaPresenter : IMediaPresenter
{
    private const double EdgeMargin = 24;
    private const double Stagger = 28;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Dictionary<Guid, MediaLayerWindow> _windows = [];

    public async Task<bool> ShowAsync(MediaLayerViewModel layer, int index)
    {
        if (layer.Target == MediaTarget.Composition)
        {
            // Shown only inside compositions, which follow the layer's visibility.
            Close(layer);
            return true;
        }

        if (_windows.TryGetValue(layer.Id, out var window) && window.IsOutput != layer.IsOutput)
        {
            Close(layer);
            window = null;
        }

        if (window is null)
        {
            window = new MediaLayerWindow(layer);
            _windows[layer.Id] = window;
            if (!await window.EnsureLoadedAsync())
            {
                Close(layer);
                return false;
            }

            window.Place(SavedBounds(layer.Item), DefaultBounds(layer, index));
        }

        if (!layer.IsShown)
        {
            return true; // hidden again while the file was loading
        }

        window.ShowLayer();
        return true;
    }

    public void Hide(MediaLayerViewModel layer)
    {
        if (_windows.TryGetValue(layer.Id, out var window))
        {
            window.HideLayer();
        }
    }

    public void Close(MediaLayerViewModel layer)
    {
        if (_windows.Remove(layer.Id, out var window))
        {
            window.Close();
        }
    }

    public void ApplyZOrder(IEnumerable<MediaLayerViewModel> backToFront)
    {
        foreach (var layer in backToFront)
        {
            if (_windows.TryGetValue(layer.Id, out var window) && !window.IsOutput && window.IsVisible)
            {
                // Each call moves the window to the top of the topmost band, so the last one ends up in front.
                NativeMethods.SetWindowPos(window.Handle, HwndTopmost, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
        }
    }

    // A monitor may have been disconnected since the layer was placed.
    private static Rect? SavedBounds(MediaItem item) =>
        ScreenPlacement.Restorable(item.Width > 0 && item.Height > 0 ? new Rect(item.Left, item.Top, item.Width, item.Height) : null);

    /// <summary>
    /// Logos and GIFs open at their own size (capped) in the top-right corner, videos at up to 560 wide;
    /// OBS outputs open 1:1 with the media (capped to 85% of the screen) near the top-left.
    /// </summary>
    private static Rect DefaultBounds(MediaLayerViewModel layer, int index)
    {
        var work = SystemParameters.WorkArea;
        var aspect = layer.ContentAspect ?? 16.0 / 9.0;
        var natural = layer.NaturalWidth > 0 ? layer.NaturalWidth * layer.Crop.Width : 640;
        var offset = (index % 6) * Stagger;

        if (layer.IsOutput)
        {
            var width = Math.Min(natural, work.Width * 0.85);
            var height = width / aspect;
            if (height > work.Height * 0.85)
            {
                height = work.Height * 0.85;
                width = height * aspect;
            }

            return new Rect(work.Left + 60 + offset, work.Top + 60 + offset, width, height);
        }

        var maxWidth = layer.IsVideo ? 560 : 360;
        var w = Math.Clamp(natural, 48, maxWidth);
        var h = w / aspect;
        if (h > work.Height * 0.6)
        {
            h = work.Height * 0.6;
            w = h * aspect;
        }

        return new Rect(work.Right - w - EdgeMargin - offset, work.Top + EdgeMargin + offset, w, h);
    }
}

/// <summary>Standard Windows "Open" dialog for media files.</summary>
public sealed class MediaFilePicker : IMediaFilePicker
{
    public IReadOnlyList<string> PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = MediaFormats.DialogFilter(),
            Title = Localization.Loc.T("Media.ImportTitle"),
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}
