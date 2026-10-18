using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VIZEN.Tests;

/// <summary>Every English string must have a Romanian translation with the same placeholders, and vice versa.</summary>
public class LocalizationTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Found from this source file's location, so the tests also work when built to another output folder.
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;

    private static Dictionary<string, string> Load(string language)
    {
        var path = Path.Combine(RepoRoot(), "VIZEN.App", "Localization", $"Strings.{language}.xaml");
        return XDocument.Load(path).Root!.Elements()
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value);
    }

    private static IEnumerable<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).Distinct().Order();

    [Fact]
    public void BothLanguagesHaveTheSameKeys()
    {
        var en = Load("en");
        var ro = Load("ro");
        Assert.Empty(en.Keys.Except(ro.Keys));
        Assert.Empty(ro.Keys.Except(en.Keys));
    }

    [Fact]
    public void TranslationsUseTheSamePlaceholders()
    {
        var en = Load("en");
        var ro = Load("ro");
        var mismatches = en.Keys.Intersect(ro.Keys)
            .Where(k => !Placeholders(en[k]).SequenceEqual(Placeholders(ro[k])))
            .ToList();
        Assert.Empty(mismatches);
    }
}
