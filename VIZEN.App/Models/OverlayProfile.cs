namespace VIZEN.App.Models;

/// <summary>A saved set of overlays that can be re-opened later.</summary>
public sealed class OverlayProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<OverlayLayout> Overlays { get; set; } = [];
}

/// <summary>
/// One overlay within a profile: how to find its source window again, and where/how to show it.
/// Bounds are in WPF device-independent units (same as Window.Left/Top/Width/Height).
/// </summary>
public sealed class OverlayLayout
{
    public string ProcessName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public string WindowTitle { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;

    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public double Opacity { get; set; } = 1.0;
    public bool ClickThrough { get; set; }

    // Crop region within the source (normalized). Defaults = whole window, so older profiles load unchanged.
    public double RegionX { get; set; }
    public double RegionY { get; set; }
    public double RegionWidth { get; set; } = 1;
    public double RegionHeight { get; set; } = 1;
    public string? RegionName { get; set; }

    /// <summary>"overlay" (Gaming) or "output" (clean OBS output window).</summary>
    public string Kind { get; set; } = KindOverlay;

    public string? OutputBackground { get; set; }
    public string? OutputFit { get; set; }

    public const string KindOverlay = "overlay";
    public const string KindOutput = "output";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOutput => Kind == KindOutput;

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Rect Region => new(RegionX, RegionY, RegionWidth, RegionHeight);

    /// <summary>Identifies the source window this layout was saved from (several regions can share one).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourceKey => $"{ExecutablePath ?? ProcessName}|{WindowTitle}|{ClassName}";
}
