using System.IO;
using System.Windows.Media.Imaging;
using VIZEN.App.Localization;
using VIZEN.App.Models;

namespace VIZEN.App.Media;

/// <summary>Supported file types and image loading (Windows Imaging Component; no extra libraries).</summary>
public static class MediaFormats
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".webp", ".tif", ".tiff", ".ico",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg",
    };

    public static MediaKind? KindFor(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Gif;
        }

        return ImageExtensions.Contains(extension) ? MediaKind.Image
            : VideoExtensions.Contains(extension) ? MediaKind.Video
            : null;
    }

    /// <summary>OpenFileDialog filter: all supported, images, GIFs, videos.</summary>
    public static string DialogFilter()
    {
        static string Patterns(IEnumerable<string> extensions) => string.Join(";", extensions.Select(e => "*" + e));
        var images = Patterns(ImageExtensions);
        var videos = Patterns(VideoExtensions);
        return $"{Loc.T("Media.FilterAll")}|{images};*.gif;{videos}"
            + $"|{Loc.T("Media.FilterImages")}|{images}"
            + $"|{Loc.T("Media.FilterGifs")}|*.gif"
            + $"|{Loc.T("Media.FilterVideos")}|{videos}";
    }

    /// <summary>
    /// Loads a still image, fully decoded and frozen (the file is not kept open). Alpha is preserved.
    /// Throws <see cref="MediaLoadException"/> with a user-facing message.
    /// </summary>
    public static BitmapSource LoadImage(string path, int decodePixelWidth = 0)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path, UriKind.Absolute);
            if (decodePixelWidth > 0)
            {
                image.DecodePixelWidth = decodePixelWidth;
            }

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new MediaLoadException(Loc.F("Media.FileMissing", path), ex);
        }
        catch (Exception ex) when (ex is NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // WebP/HEIC need the matching Windows codec (e.g. "WebP Image Extensions" from the Microsoft Store).
            throw new MediaLoadException(Loc.F("Media.NoDecoder", Path.GetExtension(path)), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileFormatException)
        {
            throw new MediaLoadException(Loc.F("Media.ReadFailed", Path.GetFileName(path), ex.Message), ex);
        }
    }
}

public sealed class MediaLoadException(string message, Exception? inner = null) : Exception(message, inner);
