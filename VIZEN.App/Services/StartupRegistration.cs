using Microsoft.Win32;

namespace VIZEN.App.Services;

/// <summary>
/// "Start with Windows": a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run (current user only,
/// no administrator rights). VIZEN starts with --tray so it opens quietly in the notification area.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VIZEN";
    public const string TrayArgument = "--tray";

    // A Store (MSIX) VIZEN is started through its app execution alias; the package folder is not for other programs.
    private static string Command => $"\"{PackagedApp.LaunchPath}\" {TrayArgument}";

    /// <summary>True when Windows will start this VIZEN executable at sign-in.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value
                       && value.Contains(PackagedApp.LaunchPath is { Length: > 0 } path ? path : "\0", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Adds or removes the sign-in entry. Returns an error message, or null on success.</summary>
    public static string? Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, Command);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName);
            }

            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return ex.Message;
        }
    }
}
