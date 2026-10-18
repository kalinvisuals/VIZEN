using VIZEN.App.Models;

namespace VIZEN.App.Services;

/// <summary>Persists overlay profiles to profiles.json in the data folder.</summary>
public sealed class ProfileStore
{
    private const string FileName = "profiles.json";

    public List<OverlayProfile> Load(out string? error)
        => JsonStore.Load<ProfilesFile>(FileName, out error)?.Profiles ?? [];

    /// <summary>Throws IOException / UnauthorizedAccessException on failure.</summary>
    public void Save(IEnumerable<OverlayProfile> profiles)
        => JsonStore.Save(FileName, new ProfilesFile { Profiles = profiles.ToList() });

    private sealed class ProfilesFile
    {
        public int Version { get; set; } = 1;
        public List<OverlayProfile> Profiles { get; set; } = [];
    }
}

public enum MatchQuality
{
    /// <summary>Same app and identical window title.</summary>
    Exact,

    /// <summary>Same app; one title contains the other (e.g. a browser tab changed).</summary>
    Similar,

    /// <summary>Same app, title differs — best available guess.</summary>
    AppOnly,

    NotFound,
}

public sealed record ProfileMatch(OverlayLayout Layout, WindowSource? Window, MatchQuality Quality);

/// <summary>
/// Finds the current window for each saved overlay. A window must belong to the same app
/// (executable path, or process name when the path is unavailable); the title then ranks candidates.
/// Layouts saved from the same window (e.g. several crop regions of one call) share one match;
/// otherwise each window is used at most once.
/// </summary>
public static class ProfileMatcher
{
    public static IReadOnlyList<ProfileMatch> Match(IReadOnlyList<OverlayLayout> layouts, IReadOnlyList<WindowSource> windows)
    {
        var used = new HashSet<IntPtr>();
        var byIndex = new ProfileMatch[layouts.Count];

        // One entry per saved source window; resolve the most specific ones first so a vague match
        // can't steal an exact one's window.
        var groups = layouts
            .Select((layout, index) => (layout, index))
            .GroupBy(x => x.layout.SourceKey)
            .Select(g => (first: g.First().layout, members: g.ToList(),
                best: windows.Max(w => (int?)Score(g.First().layout, w)) ?? 0))
            .OrderByDescending(g => g.best)
            .ToList();

        foreach (var (first, members, _) in groups)
        {
            var best = windows
                .Where(w => !used.Contains(w.Handle))
                .Select(w => (window: w, score: Score(first, w)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.window.ZOrder)
                .FirstOrDefault();

            var quality = best.window is null
                ? MatchQuality.NotFound
                : best.score switch
                {
                    >= 3 => MatchQuality.Exact,
                    2 => MatchQuality.Similar,
                    _ => MatchQuality.AppOnly,
                };

            if (best.window is not null)
            {
                used.Add(best.window.Handle);
            }

            foreach (var (layout, index) in members)
            {
                byIndex[index] = new ProfileMatch(layout, best.window, quality);
            }
        }

        return byIndex;
    }

    /// <summary>0 = different app; 1 = same app; 2 = similar title; 3 = identical title.</summary>
    private static int Score(OverlayLayout layout, WindowSource window)
    {
        var sameApp = !string.IsNullOrEmpty(layout.ExecutablePath) && !string.IsNullOrEmpty(window.ExecutablePath)
            ? string.Equals(layout.ExecutablePath, window.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(layout.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase);
        if (!sameApp)
        {
            return 0;
        }

        if (string.Equals(layout.WindowTitle, window.Title, StringComparison.Ordinal))
        {
            return 3;
        }

        if (layout.WindowTitle.Length > 0
            && (window.Title.Contains(layout.WindowTitle, StringComparison.OrdinalIgnoreCase)
                || layout.WindowTitle.Contains(window.Title, StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        return 1;
    }
}
