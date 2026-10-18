namespace VIZEN.App.Capture;

/// <summary>
/// What window captures need from the rest of the app: the per-app "Keep source active" preference and
/// a place to report source-window events to the user. UI thread only.
/// </summary>
public interface ISourceWindowHost
{
    /// <summary>Raised when any app's "Keep source active" preference changes.</summary>
    event EventHandler? KeepActiveChanged;

    bool IsKeepActive(string appKey);

    void SetKeepActive(string appKey, string sourceTitle, bool enabled);

    /// <summary>The captured window was minimized and capture is paused (keep-active is off).</summary>
    void ReportMinimized(string sourceTitle);

    /// <summary>The window was restored behind the active window, automatically or on request.</summary>
    void ReportRestored(string sourceTitle, bool automatic);

    void ReportShown(string sourceTitle, bool succeeded);

    /// <summary>The GPU device was lost; <paramref name="captures"/> window captures were restarted (or <paramref name="error"/>).</summary>
    void ReportDeviceRecovered(int captures, string? error);
}
