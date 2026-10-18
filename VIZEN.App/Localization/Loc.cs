using System.Globalization;
using System.Windows;
using VIZEN.App.Infrastructure;

namespace VIZEN.App.Localization;

public sealed record LanguageOption(string Code, string NativeName);

/// <summary>
/// UI localization. Strings live in Localization/Strings.{code}.xaml. English is always merged (App.xaml)
/// as the fallback; another language is merged on top, so a missing translation shows English, never a blank.
/// XAML uses {DynamicResource Key} and updates live; view models use <see cref="T"/>/<see cref="F"/> and are
/// told to refresh their bindings when the language changes.
/// </summary>
public static class Loc
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<ObservableObject>> Subscribers = [];
    private static ResourceDictionary? _overlay;
    private static int _addsSincePrune;

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("en", "English"),
        new("ro", "Română"),
    ];

    public static string CurrentLanguage { get; private set; } = "en";

    /// <summary>Romanian when Windows is installed in Romanian, otherwise English.</summary>
    public static string DefaultLanguage =>
        CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ro" ? "ro" : "en";

    public static string Normalize(string? code) =>
        Languages.Any(l => l.Code == code) ? code! : DefaultLanguage;

    /// <summary>Switches the UI language. UI thread only.</summary>
    public static void Apply(string? code)
    {
        code = Normalize(code);
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_overlay is not null)
        {
            merged.Remove(_overlay);
            _overlay = null;
        }

        if (code != "en")
        {
            _overlay = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/VIZEN.App;component/Localization/Strings.{code}.xaml"),
            };
            merged.Add(_overlay);
        }

        CurrentLanguage = code;
        var culture = CultureInfo.GetCultureInfo(code == "ro" ? "ro-RO" : "en-US");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        NotifySubscribers();
    }

    /// <summary>Localized string for <paramref name="key"/>, or the key itself if it is missing.</summary>
    public static string T(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    /// <summary>Localized composite format string for <paramref name="key"/>, filled with <paramref name="args"/>.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    internal static void Subscribe(ObservableObject target)
    {
        lock (Gate)
        {
            Subscribers.Add(new WeakReference<ObservableObject>(target));
            if (++_addsSincePrune >= 256)
            {
                _addsSincePrune = 0;
                Subscribers.RemoveAll(r => !r.TryGetTarget(out _));
            }
        }
    }

    private static void NotifySubscribers()
    {
        List<ObservableObject> alive;
        lock (Gate)
        {
            Subscribers.RemoveAll(r => !r.TryGetTarget(out _));
            alive = Subscribers.Select(r => r.TryGetTarget(out var t) ? t : null).OfType<ObservableObject>().ToList();
        }

        foreach (var target in alive)
        {
            target.RaiseAllPropertiesChanged();
        }
    }
}
