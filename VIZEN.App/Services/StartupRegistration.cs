using Microsoft.Win32;
using VIZEN.App.Localization;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace VIZEN.App.Services;

/// <summary>
/// "Start with Windows". Installer VIZEN: a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run (current user
/// only, no administrator rights, removed by the uninstaller), started with --tray so it opens quietly in the
/// notification area. Store (MSIX) VIZEN: the package's startup task (installer\msix\AppxManifest.xml), which Windows
/// lists under Task Manager > Startup apps and removes with the package, so an uninstall leaves no startup entry behind.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VIZEN";
    public const string TrayArgument = "--tray";

    /// <summary>The TaskId of the startup task declared in the package manifest.</summary>
    public const string StartupTaskId = "VIZENStartup";

    private static string Command => $"\"{PackagedApp.LaunchPath}\" {TrayArgument}";

    /// <summary>True when Windows will start this VIZEN at sign-in.</summary>
    public static bool IsEnabled
    {
        get
        {
            if (PackagedApp.IsPackaged)
            {
                return GetTask() is { State: StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy };
            }

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

    /// <summary>True when Windows started this Store VIZEN through its startup task (the task passes no arguments).</summary>
    public static bool StartedByStartupTask()
    {
        if (!PackagedApp.IsPackaged)
        {
            return false;
        }

        try
        {
            return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>Adds or removes the sign-in entry. Returns an error message, or null on success.</summary>
    public static string? Set(bool enabled) => PackagedApp.IsPackaged ? SetTask(enabled) : SetRunValue(enabled);

    private static string? SetRunValue(bool enabled)
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

    private static string? SetTask(bool enabled)
    {
        RemoveAliasRunValue();
        var task = GetTask();
        if (task is null)
        {
            return Loc.T("Activity.LaunchTaskMissing");
        }

        try
        {
            if (!enabled)
            {
                task.Disable();
                return null;
            }

            return task.RequestEnableAsync().AsTask().GetAwaiter().GetResult() switch
            {
                StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => null,
                StartupTaskState.DisabledByUser => Loc.T("Activity.LaunchDisabledByUser"),
                StartupTaskState.DisabledByPolicy => Loc.T("Activity.LaunchDisabledByPolicy"),
                var state => state.ToString(),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return ex.Message;
        }
    }

    private static StartupTask? GetTask()
    {
        try
        {
            return StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null; // not declared in this package
        }
    }

    /// <summary>A Run value that starts the Store VIZEN through its alias would break once the package is removed.</summary>
    private static void RemoveAliasRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string value && value.Contains(PackagedApp.LaunchPath, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(ValueName);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Left as it is; the task is what starts VIZEN.
        }
    }
}
