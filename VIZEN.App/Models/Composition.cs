using System.Text.Json.Serialization;
using System.Windows;

namespace VIZEN.App.Models;

public enum CompositionItemKind
{
    /// <summary>A live window/camera capture, optionally cropped to a region.</summary>
    Capture,

    /// <summary>A media layer (logo, GIF, video) from the library.</summary>
    Media,
}

/// <summary>One layer inside a composition. Position and size are normalized to the canvas (0..1).</summary>
public sealed class CompositionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CompositionItemKind Kind { get; set; }

    public double X { get; set; } = 0.05;
    public double Y { get; set; } = 0.05;
    public double Width { get; set; } = 0.4;
    public double Height { get; set; } = 0.4;
    public double Opacity { get; set; } = 1;

    // Capture: how to find the source again (same rules as profiles) and which part of it to show.
    public string ProcessName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public string WindowTitle { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public double RegionX { get; set; }
    public double RegionY { get; set; }
    public double RegionWidth { get; set; } = 1;
    public double RegionHeight { get; set; } = 1;
    public string? RegionName { get; set; }

    // Media: the library layer, and whether it is only shown while that layer is "shown" (e.g. by a schedule).
    public Guid? MediaLayerId { get; set; }
    public bool FollowLayerVisibility { get; set; } = true;

    [JsonIgnore]
    public Rect Bounds
    {
        get => new(X, Y, Width, Height);
        set
        {
            var w = Math.Clamp(value.Width, 0.02, 1);
            var h = Math.Clamp(value.Height, 0.02, 1);
            (X, Y, Width, Height) = (Math.Clamp(value.X, -w + 0.02, 0.98), Math.Clamp(value.Y, -h + 0.02, 0.98), w, h);
        }
    }

    [JsonIgnore]
    public Rect Region => new(RegionX, RegionY, RegionWidth, RegionHeight);

    /// <summary>The capture identity in the shape the profile matcher understands.</summary>
    public OverlayLayout ToLayout() => new()
    {
        ProcessName = ProcessName,
        ExecutablePath = ExecutablePath,
        WindowTitle = WindowTitle,
        ClassName = ClassName,
        RegionX = RegionX,
        RegionY = RegionY,
        RegionWidth = RegionWidth,
        RegionHeight = RegionHeight,
        RegionName = RegionName,
    };
}

/// <summary>
/// A scene: several captures and media layers stacked on one canvas, shown in a single clean window for
/// OBS Window Capture. Items are stored front first. Persisted in compositions.json.
/// </summary>
public sealed class Composition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public int CanvasWidth { get; set; } = 1920;
    public int CanvasHeight { get; set; } = 1080;

    /// <summary>"black" or "green" (chroma key).</summary>
    public string Background { get; set; } = "black";

    /// <summary>Open the composition window automatically when VIZEN starts (so OBS always finds it).</summary>
    public bool AutoOpen { get; set; }

    // Last window bounds (DIPs); 0 = not placed yet.
    public double Left { get; set; }
    public double Top { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    public List<CompositionItem> Items { get; set; } = [];
}
