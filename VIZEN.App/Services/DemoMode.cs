namespace VIZEN.App.Services;

/// <summary>
/// Documentation screenshots only: started with VIZEN_DEMO=1, VIZEN lists only its own windows and the demo windows
/// (titles starting with "VIZEN" or "Demo"), only the apps named in VIZEN_DEMO_AUDIO (e.g. "chrome.exe") in the mixer,
/// shows no audio device names and no cameras, and uses its own browser pipe, so the screenshots never show what else
/// is open on the PC that takes them, or its hardware. Off for everyone else.
/// </summary>
public static class DemoMode
{
    public static bool IsOn { get; } = Environment.GetEnvironmentVariable("VIZEN_DEMO") == "1";

    private static readonly string[] AudioApps = (Environment.GetEnvironmentVariable("VIZEN_DEMO_AUDIO") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool ShowsWindow(string title) =>
        !IsOn || title.StartsWith("VIZEN", StringComparison.Ordinal) || title.StartsWith("Demo", StringComparison.Ordinal);

    public static bool ShowsAudioApp(string key) =>
        !IsOn || AudioApps.Any(a => key.Equals(a, StringComparison.OrdinalIgnoreCase) || key.EndsWith("\\" + a, StringComparison.OrdinalIgnoreCase));

    /// <summary>Added to the browser pipe name, so a demo VIZEN and the user's VIZEN never share browser connections.</summary>
    public static string PipeSuffix => IsOn ? ".demo" : string.Empty;
}
