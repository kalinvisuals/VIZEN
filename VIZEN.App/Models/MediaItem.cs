using System.Text.Json.Serialization;
using System.Windows;

namespace VIZEN.App.Models;

public enum MediaKind
{
    Image,
    Gif,
    Video,
}

/// <summary>Where a media layer is shown: over everything (Gaming) or as a clean window for OBS.</summary>
public enum MediaTarget
{
    Overlay,
    Output,

    /// <summary>No window of its own: shown only inside compositions (schedules still show/hide it there).</summary>
    Composition,
}

public enum MediaTransition
{
    None,
    Fade,
    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,
    Zoom,
}

/// <summary>
/// One imported media file (logo, GIF or video) and how it is shown. Persisted in media.json.
/// Bounds are WPF device-independent units; 0 width/height = not placed yet.
/// </summary>
public sealed class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public MediaKind Kind { get; set; }

    /// <summary>Optional group (e.g. "Sponsors"), used to show, hide or lock several layers together.</summary>
    public string? Group { get; set; }

    public MediaTarget Target { get; set; } = MediaTarget.Overlay;

    /// <summary>OBS output background: "black", "green" (chroma key) or "magenta".</summary>
    public string OutputBackground { get; set; } = "green";

    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public double Opacity { get; set; } = 1.0;

    /// <summary>Clockwise rotation in degrees, -180..180.</summary>
    public double Rotation { get; set; }

    /// <summary>Locked layers cannot be moved or resized with the mouse.</summary>
    public bool Locked { get; set; }

    public bool ClickThrough { get; set; }

    /// <summary>Show the accent border and title bar when the mouse is over the layer (overlays only).</summary>
    public bool ShowBorder { get; set; } = true;

    /// <summary>Stacking order among media overlays; higher is in front.</summary>
    public int Layer { get; set; }

    // Normalized crop of the media (0..1). Defaults = whole file.
    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropWidth { get; set; } = 1;
    public double CropHeight { get; set; } = 1;

    /// <summary>GIF and video: loop forever, or play once and hold the last frame.</summary>
    public bool Loop { get; set; } = true;

    public bool Muted { get; set; }
    public double Volume { get; set; } = 0.8;

    public MediaTransition TransitionIn { get; set; } = MediaTransition.Fade;
    public MediaTransition TransitionOut { get; set; } = MediaTransition.Fade;
    public double TransitionSeconds { get; set; } = 0.5;

    [JsonIgnore]
    public Rect Crop
    {
        get => new(CropX, CropY, CropWidth, CropHeight);
        set
        {
            var r = RegionMath.ClampInside(value);
            (CropX, CropY, CropWidth, CropHeight) = (r.X, r.Y, r.Width, r.Height);
        }
    }

    public MediaItem Clone() => (MediaItem)MemberwiseClone();
}
