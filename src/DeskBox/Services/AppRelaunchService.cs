using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace DeskBox.Services;

public static class AppRelaunchService
{
    // Store builds do not ship DeskBox.Updater (excluded from Store packaging),
    // so the relaunch is delegated to the WinAppSDK restart agent from the
    // graceful-shutdown tail instead of a detached helper process.
    public static bool PendingOsRestartOnShutdown { get; set; }

    /// <summary>
    /// Store builds relaunch through the WinAppSDK restart agent; when the
    /// restart came from the idle process recycle it must keep the unattended
    /// startup contract (no settings window, no forced widget reveal).
    /// </summary>
    public static bool PendingBackgroundRestartOnShutdown { get; set; }

    public static AppRelaunchScheduleResult ScheduleAfterCurrentProcessExit(
        bool startInBackground = false)
    {
        if (AppDistributionService.Current.IsMicrosoftStore)
        {
            PendingOsRestartOnShutdown = true;
            PendingBackgroundRestartOnShutdown = startInBackground;
            return AppRelaunchScheduleResult.StartedSuccessfully;
        }

        string appPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeskBox.exe");
        if (!File.Exists(appPath))
        {
            return AppRelaunchScheduleResult.Failed("DeskBox.exe was not found.");
        }

        try
        {
            string helperPath = AppUpdateService.PrepareDetachedUpdaterHelper(
                AppContext.BaseDirectory,
                DeskBoxDataPathService.Current.UpdatesDirectory);
            if (!File.Exists(helperPath))
            {
                return AppRelaunchScheduleResult.Failed("DeskBox.Updater.exe was not found.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(helperPath) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--restart-only");
            startInfo.ArgumentList.Add("--pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--app");
            startInfo.ArgumentList.Add(appPath);
            if (startInBackground)
            {
                startInfo.ArgumentList.Add("--app-arg");
                startInfo.ArgumentList.Add("--startup");
                startInfo.ArgumentList.Add("--app-arg");
                startInfo.ArgumentList.Add("--lazy-restore");
            }

            Process.Start(startInfo);
            return AppRelaunchScheduleResult.StartedSuccessfully;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       InvalidOperationException or Win32Exception)
        {
            App.Log($"[Relaunch] Failed to schedule app restart: {ex}");
            return AppRelaunchScheduleResult.Failed(ex.Message);
        }
    }
}

public sealed record AppRelaunchScheduleResult(bool Started, string? ErrorMessage)
{
    public static AppRelaunchScheduleResult StartedSuccessfully { get; } = new(true, null);

    public static AppRelaunchScheduleResult Failed(string errorMessage) => new(false, errorMessage);
}
