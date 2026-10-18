using System.IO;
using System.Text.Json;
using VIZEN.App.Localization;

namespace VIZEN.App.Services;

/// <summary>
/// Reads and writes small JSON documents in VIZEN's per-user data folder.
/// Writes are atomic (temp file + replace) so a crash mid-save can't truncate the file.
/// </summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// %LOCALAPPDATA%\VIZEN, or the folder in the VIZEN_DATA_DIR environment variable (a separate test or
    /// portable profile). Unit tests redirect it to a temporary folder.
    /// </summary>
    public static string DataDirectory { get; internal set; } =
        Environment.GetEnvironmentVariable("VIZEN_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIZEN");

    /// <summary>
    /// Loads <paramref name="fileName"/>. Returns default when the file doesn't exist.
    /// An unreadable or corrupt file yields default plus an <paramref name="error"/>; a corrupt file
    /// is copied aside as *.corrupt so the user's data isn't silently overwritten.
    /// </summary>
    public static T? Load<T>(string fileName, out string? error)
    {
        error = null;
        var path = Path.Combine(DataDirectory, fileName);
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (JsonException ex)
        {
            error = Loc.F("Storage.Corrupt", fileName, ex.Message);
            TryCopy(path, path + ".corrupt");
            return default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = Loc.F("Storage.ReadFailed", fileName, ex.Message);
            return default;
        }
    }

    /// <summary>Saves <paramref name="value"/>. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> on failure.</summary>
    public static void Save<T>(string fileName, T value)
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, fileName);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, Options));
        File.Move(tempPath, path, overwrite: true);
    }

    private static void TryCopy(string source, string destination)
    {
        try
        {
            File.Copy(source, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }
}
