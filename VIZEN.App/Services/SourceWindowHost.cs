using System.IO;
using VIZEN.App.Capture;

namespace VIZEN.App.Services;

/// <summary>
/// Connects window captures to the saved "Keep source active" preference and to the activity log/toasts.
/// </summary>
public sealed class SourceWindowHost : ISourceWindowHost
{
    private readonly AppSettings _settings;
    private readonly ActivityLog _activity;

    public SourceWindowHost(AppSettings settings, ActivityLog activity)
    {
        _settings = settings;
        _activity = activity;
    }

    public event EventHandler? KeepActiveChanged;

    public bool IsKeepActive(string appKey) => _settings.IsKeepActive(appKey);

    public void SetKeepActive(string appKey, string sourceTitle, bool enabled)
    {
        _settings.SetKeepActive(appKey, enabled);
        KeepActiveChanged?.Invoke(this, EventArgs.Empty);
        _activity.Add("", enabled ? "Activity.KeepActiveOn" : "Activity.KeepActiveOff",
            enabled ? "Activity.KeepActiveOnDetail" : "Activity.KeepActiveOffDetail",
            AppName(appKey), sourceTitle);
    }

    public void ReportMinimized(string sourceTitle) =>
        _activity.Add("", "Activity.SourceMinimized", "Activity.SourceMinimizedDetail", sourceTitle);

    public void ReportRestored(string sourceTitle, bool automatic) =>
        _activity.Add("", automatic ? "Activity.SourceKeptActive" : "Activity.SourceRestored",
            automatic ? "Activity.SourceKeptActiveDetail" : "Activity.SourceRestoredDetail", sourceTitle);

    public void ReportShown(string sourceTitle, bool succeeded) =>
        _activity.Add("", succeeded ? "Activity.SourceShown" : "Activity.SourceShowFailed",
            succeeded ? "Activity.SourceShownDetail" : "Activity.SourceShowFailedDetail", sourceTitle);

    public void ReportDeviceRecovered(int captures, string? error) =>
        _activity.Add("\uE7F4", error is null ? "Activity.DeviceRecovered" : "Activity.DeviceRecoveryFailed",
            error is null ? "Activity.DeviceRecoveredDetail" : "Activity.DeviceRecoveryFailedDetail", captures, error);

    private static string AppName(string appKey) =>
        appKey.Contains('\\') ? Path.GetFileNameWithoutExtension(appKey) : appKey;
}
