using System.IO;
using System.Runtime.InteropServices;

namespace VIZEN.App.Services;

/// <summary>
/// VIZEN installed from the Microsoft Store (MSIX package) or with the classic installer. A packaged VIZEN lives in
/// the protected WindowsApps folder: other programs (the browser starting the extension's native host, Windows starting
/// VIZEN at sign-in) start it through its app execution alias "vizen.exe", and the extension files are copied to the
/// data folder so the user can open them.
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

    /// <summary>
    /// The VIZEN Connector folder the user can open and load in a browser: next to the program, or for a packaged
    /// VIZEN a copy in the data folder (refreshed when the program's copy changes).
    /// </summary>
    public static string ExtensionFolder(string dataDirectory)
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "browser-extension");
        if (!IsPackaged || !Directory.Exists(shipped))
        {
            return shipped;
        }

        var copy = Path.Combine(dataDirectory, "browser-extension");
        try
        {
            foreach (var file in Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(shipped, file));
                if (!File.Exists(target) || File.GetLastWriteTimeUtc(target) != File.GetLastWriteTimeUtc(file))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
                }
            }

            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return shipped;
        }
    }

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
