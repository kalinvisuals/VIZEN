using VIZEN.App.Models;

namespace VIZEN.App.Services;

/// <summary>A crop region as stored on disk (normalized coordinates).</summary>
public sealed class SavedRegion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;

    /// <summary>Locked pixel aspect ratio (width / height), or null for free-form.</summary>
    public double? Aspect { get; set; }
}

/// <summary>
/// Shared source library: crop regions remembered per application (executable path, or process name
/// when the path is unavailable), stored in regions.json. Used by all modes.
/// </summary>
public sealed class RegionLibrary
{
    private const string FileName = "regions.json";
    private readonly Dictionary<string, List<SavedRegion>> _byApp;

    public RegionLibrary(out string? loadError)
    {
        var file = JsonStore.Load<RegionsFile>(FileName, out loadError);
        _byApp = new Dictionary<string, List<SavedRegion>>(file?.Apps ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public static string KeyFor(WindowSource source) => source.ExecutablePath ?? source.ProcessName;

    public IReadOnlyList<SavedRegion> Get(WindowSource source) =>
        _byApp.TryGetValue(KeyFor(source), out var regions) ? regions : [];

    /// <summary>Replaces the regions remembered for <paramref name="source"/>'s app. Throws on I/O failure.</summary>
    public void Set(WindowSource source, IEnumerable<SavedRegion> regions)
    {
        var list = regions.ToList();
        if (list.Count == 0)
        {
            _byApp.Remove(KeyFor(source));
        }
        else
        {
            _byApp[KeyFor(source)] = list;
        }

        JsonStore.Save(FileName, new RegionsFile { Apps = new Dictionary<string, List<SavedRegion>>(_byApp) });
    }

    private sealed class RegionsFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, List<SavedRegion>> Apps { get; set; } = [];
    }
}
