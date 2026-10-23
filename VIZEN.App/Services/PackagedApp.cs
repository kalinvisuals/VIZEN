using System.IO;
using System.Runtime.InteropServices;

namespace VIZEN.App.Services;

/// <summary>
/// VIZEN installed from the Microsoft Store (MSIX package) or with the classic installer. A packaged VIZEN lives in
/// the protected WindowsApps folder: other programs (the browser starting the extension's native host, Windows starting
/// VIZEN at sign-in) start it through its app execution alias "vizen.exe".
/// </summary>
public static class PackagedApp
{
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>The alias declared in the package manifest (installer\msix\AppxManifest.xml).</summary>
    public const string AliasName = "vizen.exe";

    public static bool IsPackaged { get; } = DetectPackage();

    /// <summary>The path other programs use to start this VIZEN.</summary>
    public static string LaunchPath => IsPackaged
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", AliasName)
        : Environment.ProcessPath ?? string.Empty;

    private static bool DetectPackage()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
