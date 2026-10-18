using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace VIZEN.App.Services;

/// <summary>
/// Applies the UI theme (dark / light / follow Windows) and the accent colour by replacing the shared brushes in
/// the application resources. Every view references them as DynamicResource, so changes apply instantly.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "dark";
    public const string Light = "light";
    public const string System = "system";

    /// <summary>Accent name → (accent, second gradient colour).</summary>
    public static IReadOnlyDictionary<string, (Color Accent, Color Second)> Accents { get; } = new Dictionary<string, (Color, Color)>
    {
        ["red"] = (Rgb(0xFF, 0x35, 0x4E), Rgb(0xFF, 0x52, 0x68)),
        ["blue"] = (Rgb(0x2F, 0x80, 0xFF), Rgb(0x00, 0xC8, 0xFF)),
        ["teal"] = (Rgb(0x14, 0xB8, 0x9A), Rgb(0x0E, 0xA5, 0xE9)),
        ["rose"] = (Rgb(0xF0, 0x56, 0x6A), Rgb(0x8B, 0x5C, 0xF6)),
        ["amber"] = (Rgb(0xE8, 0xA0, 0x2E), Rgb(0xF0, 0x56, 0x6A)),
    };

    private static readonly Dictionary<string, Color> DarkPalette = new()
    {
        ["Background"] = Rgb(0x0A, 0x0D, 0x14),
        ["Sidebar"] = Rgb(0x0D, 0x11, 0x19),
        ["Surface"] = Rgb(0x12, 0x17, 0x22),
        ["SurfaceRaised"] = Rgb(0x17, 0x1D, 0x2A),
        ["SurfaceHover"] = Rgb(0x1D, 0x24, 0x34),
        ["Border"] = Rgb(0x1F, 0x27, 0x37),
        ["BorderStrong"] = Rgb(0x2C, 0x36, 0x4C),
        ["TextPrimary"] = Rgb(0xE9, 0xED, 0xF5),
        ["TextSecondary"] = Rgb(0x98, 0xA2, 0xB7),
        ["TextTertiary"] = Rgb(0x5F, 0x69, 0x80),
        ["Success"] = Rgb(0x34, 0xD3, 0x99),
        ["Warning"] = Rgb(0xF5, 0xB5, 0x47),
        ["Danger"] = Rgb(0xE5, 0x48, 0x4D),
    };

    private static readonly Dictionary<string, Color> LightPalette = new()
    {
        ["Background"] = Rgb(0xF3, 0xF5, 0xF9),
        ["Sidebar"] = Rgb(0xEA, 0xEE, 0xF4),
        ["Surface"] = Rgb(0xFF, 0xFF, 0xFF),
        ["SurfaceRaised"] = Rgb(0xF4, 0xF6, 0xFA),
        ["SurfaceHover"] = Rgb(0xE6, 0xEB, 0xF3),
        ["Border"] = Rgb(0xDA, 0xE0, 0xEA),
        ["BorderStrong"] = Rgb(0xC0, 0xC8, 0xD7),
        ["TextPrimary"] = Rgb(0x14, 0x1A, 0x26),
        ["TextSecondary"] = Rgb(0x48, 0x52, 0x66),
        ["TextTertiary"] = Rgb(0x78, 0x82, 0x96),
        ["Success"] = Rgb(0x12, 0x9E, 0x6B),
        ["Warning"] = Rgb(0xB8, 0x74, 0x0B),
        ["Danger"] = Rgb(0xD3, 0x37, 0x3D),
    };

    private static string _theme = Dark;
    private static string _accent = "red";
    private static bool _listening;

    /// <summary>True when the effective theme is dark (after resolving "system").</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised after the theme or accent changed (e.g. so windows can update their frame).</summary>
    public static event EventHandler? Changed;

    public static string NormalizeTheme(string? theme) => theme is Light or System ? theme : Dark;

    public static string NormalizeAccent(string? accent) => accent is not null && Accents.ContainsKey(accent) ? accent : "red";

    /// <summary>Whether Windows' "app mode" is dark (Settings → Personalization → Colors).</summary>
    public static bool IsWindowsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch (Exception ex) when (ex is global::System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static void Apply(string? theme, string? accent)
    {
        _theme = NormalizeTheme(theme);
        _accent = NormalizeAccent(accent);
        if (_theme == System && !_listening)
        {
            // Follow Windows when the user switches its light/dark mode.
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }

        ApplyCurrent();
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_theme == System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            Application.Current?.Dispatcher.BeginInvoke(ApplyCurrent);
        }
    }

    private static void ApplyCurrent()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        IsDark = _theme switch
        {
            Light => false,
            System => IsWindowsDark(),
            _ => true,
        };

        var palette = IsDark ? DarkPalette : LightPalette;
        var resources = app.Resources;
        foreach (var (name, color) in palette)
        {
            resources["Brush." + name] = Freeze(new SolidColorBrush(color));
        }

        var (accent, second) = Accents[_accent];
        resources["Brush.Accent"] = Freeze(new SolidColorBrush(accent));
        resources["Brush.AccentViolet"] = Freeze(new SolidColorBrush(second));
        resources["Brush.AccentSoft"] = Freeze(new SolidColorBrush(WithAlpha(accent, (byte)(IsDark ? 0x22 : 0x1C))));
        resources["Brush.VioletSoft"] = Freeze(new SolidColorBrush(WithAlpha(second, 0x22)));
        resources["Brush.Selection"] = Freeze(new SolidColorBrush(WithAlpha(accent, (byte)(IsDark ? 0x1A : 0x16))));
        resources["Brush.SuccessSoft"] = Freeze(new SolidColorBrush(WithAlpha(palette["Success"], 0x1F)));
        resources["Brush.WarningSoft"] = Freeze(new SolidColorBrush(WithAlpha(palette["Warning"], 0x1F)));
        resources["Brush.AccentGradient"] = Freeze(Gradient(new Point(1, 1), accent, second));
        resources["Brush.AccentGradientVertical"] = Freeze(Gradient(new Point(0, 1), accent, second));
        resources["Brush.GlowBlue"] = Freeze(Glow(accent, 0x55));
        resources["Brush.GlowViolet"] = Freeze(Glow(second, 0x4A));
        resources["Brush.HeroGradient"] = Freeze(IsDark
            ? Gradient(new Point(1, 1), Rgb(0x15, 0x1A, 0x24), Mix(Rgb(0x15, 0x1A, 0x24), accent, 0.20))
            : Gradient(new Point(1, 1), Rgb(0xF6, 0xF7, 0xFA), Mix(Rgb(0xFF, 0xFF, 0xFF), accent, 0.10)));

        // Smart Audio Mixer: the app's own colours (so the page blends with the rest of VIZEN), with hairline borders
        // and inverted primary actions (light pill on dark, dark pill on light).
        var ink = palette["TextPrimary"];
        resources["Brush.Mx.Bg"] = Freeze(new SolidColorBrush(palette["Background"]));
        resources["Brush.Mx.Panel"] = Freeze(new SolidColorBrush(palette["Surface"]));
        resources["Brush.Mx.Raised"] = Freeze(new SolidColorBrush(palette["SurfaceRaised"]));
        resources["Brush.Mx.Line"] = Freeze(new SolidColorBrush(palette["Border"]));
        resources["Brush.Mx.LineStrong"] = Freeze(new SolidColorBrush(palette["BorderStrong"]));
        resources["Brush.Mx.Text"] = Freeze(new SolidColorBrush(ink));
        resources["Brush.Mx.Muted"] = Freeze(new SolidColorBrush(palette["TextSecondary"]));
        resources["Brush.Mx.Faint"] = Freeze(new SolidColorBrush(palette["TextTertiary"]));
        resources["Brush.Mx.Fill"] = Freeze(new SolidColorBrush(WithAlpha(ink, (byte)(IsDark ? 0x24 : 0x14))));
        resources["Brush.Mx.Invert"] = Freeze(new SolidColorBrush(ink));
        resources["Brush.Mx.InvertText"] = Freeze(new SolidColorBrush(palette["Background"]));
        resources["Brush.Mx.Grid"] = Freeze(new SolidColorBrush(WithAlpha(ink, (byte)(IsDark ? 0x09 : 0x08))));
        // Accent light at the top of the mixer page, fading down to nothing (follows the chosen accent colour).
        // Kept very soft (a hint of colour, not a red block): long, smooth fall-off.
        resources["Brush.Mx.AccentFade"] = Freeze(new LinearGradientBrush(
            new GradientStopCollection
            {
                new(WithAlpha(accent, (byte)(IsDark ? 0x14 : 0x0C)), 0),
                new(WithAlpha(accent, (byte)(IsDark ? 0x09 : 0x05)), 0.35),
                new(WithAlpha(accent, (byte)(IsDark ? 0x03 : 0x02)), 0.7),
                new(WithAlpha(accent, 0), 1),
            },
            new Point(0, 0), new Point(0, 1)));
        resources["Brush.Mx.AccentGlow"] = Freeze(new RadialGradientBrush(
            new GradientStopCollection
            {
                new(WithAlpha(accent, (byte)(IsDark ? 0x22 : 0x14)), 0),
                new(WithAlpha(accent, (byte)(IsDark ? 0x0C : 0x07)), 0.5),
                new(WithAlpha(accent, 0), 1),
            }));
        // A 1 px accent line along the top edge, fading out towards the sides.
        resources["Brush.Mx.AccentLine"] = Freeze(new LinearGradientBrush(
            new GradientStopCollection
            {
                new(WithAlpha(accent, 0), 0),
                new(WithAlpha(accent, (byte)(IsDark ? 0x99 : 0x77)), 0.5),
                new(WithAlpha(accent, 0), 1),
            },
            new Point(0, 0), new Point(1, 0)));

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Blends <paramref name="b"/> into <paramref name="a"/> by <paramref name="t"/> (0 = a, 1 = b).</summary>
    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static LinearGradientBrush Gradient(Point end, Color from, Color to) =>
        new(from, to, new Point(0, 0), end);

    private static RadialGradientBrush Glow(Color color, byte alpha) =>
        new(WithAlpha(color, alpha), WithAlpha(color, 0));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
