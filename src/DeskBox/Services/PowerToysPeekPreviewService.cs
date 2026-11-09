using System.Diagnostics;
using DeskBox.Platform;
using Microsoft.Win32;

namespace DeskBox.Services;

/// <summary>
/// Passively forwards file previews to an already-running PowerToys Peek
/// instance. Peek gained a command-line activation surface in PowerToys
/// 0.96 (microsoft/PowerToys#38754): launching
/// "PowerToys.Peek.UI.exe &lt;path&gt;" opens a preview, re-sending the same
/// path closes it, and a different path switches — the same contract the
/// QuickLook pipe exposes, so the WidgetManager session state machine can
/// drive both backends unchanged.
///
/// Like QuickLookPreviewService this never discovers, installs, or
/// configures the other app: a running Peek module process is the only
/// availability signal (the PowerToys runner keeps that process alive
/// exactly while the module is enabled, which also honors a user who
/// disabled Peek). Version probing fails closed because a pre-0.96
/// install also runs the same process and would silently ignore the path
/// argument while spawning a useless extra process on every keypress.
///
/// Security note: the activation forwards a locally validated file path
/// (existence-checked; quote-free by Windows path rules) to a fixed,
/// resolved-by-probe executable through ShellUnelevatedLaunch — no shell,
/// no user-controlled executable, no unescaped concatenation. Audited
/// against the command-injection false-positive family in
/// artifacts/mimosa-command-injection-audit-20261021/report.md.
/// </summary>
public sealed class PowerToysPeekPreviewService
{
    internal const string PeekProcessName = "PowerToys.Peek.UI";
    internal const string PeekExecutableName = "PowerToys.Peek.UI.exe";

    // Command-line activation shipped in PowerToys 0.96 (merged 2025-11).
    private static readonly Version MinimumVersion = new(0, 96);

    // Peek's window takes a few dozen ms to appear after a toggle
    // activation; a visibility probe inside that window would misread an
    // open preview as closed and end a live session, so switches that
    // follow a successful toggle within this period are sent unprobed.
    internal const int ToggleGracePeriodMs = 300;

    private const string WinUi3AppsFolder = "WinUI3Apps";

    // Positive resolution cache (executable path that passed the version
    // gate), per process lifetime. Negative results stay uncached: a
    // PowerToys upgrade mid-session should turn an old "no" into a "yes"
    // without restarting DeskBox, and a positive result can only get
    // stale in the safe direction (an upgrade keeps version >= 0.96).
    private static string? s_cachedExecutablePath;

    private static bool s_integrityHintLogged;

    // UI-thread confined, like every other preview call site.
    private long _lastToggleStartedTicks;

    public bool CanPreview(string? path) =>
        QuickLookPreviewService.IsPreviewablePath(path) && IsAvailable();

    public Task<bool> TryToggleAsync(string path)
    {
        if (!QuickLookPreviewService.IsPreviewablePath(path))
        {
            App.Log($"[Peek] Path not previewable: '{path}'.");
            return Task.FromResult(false);
        }

        bool sent = TryActivate(path);
        if (sent)
        {
            _lastToggleStartedTicks = Environment.TickCount64;
        }

        return Task.FromResult(sent);
    }

    /// <summary>
    /// Changes the item shown by an existing Peek preview. Peek has no
    /// explicit switch command — re-sending a different path switches —
    /// but it also has no "ignore while closed" behavior, so the window
    /// visibility probe keeps a user-closed preview from being reopened
    /// by selection follow updates.
    /// </summary>
    public Task<bool> TrySwitchAsync(string path)
    {
        if (!QuickLookPreviewService.IsPreviewablePath(path))
        {
            App.Log($"[Peek] Path not previewable: '{path}'.");
            return Task.FromResult(false);
        }

        bool send = ShouldSendSwitch(
            IsPeekWindowVisible(),
            _lastToggleStartedTicks,
            Environment.TickCount64);
        if (!send)
        {
            App.Log("[Peek] Preview window not visible; switch suppressed.");
            return Task.FromResult(false);
        }

        return Task.FromResult(TryActivate(path));
    }

    /// <summary>
    /// Peek has no close command; re-sending the path the session last
    /// showed toggles the open preview closed. When the window is already
    /// gone there is nothing to send.
    /// </summary>
    public Task<bool> TryCloseAsync(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath) || !IsPeekWindowVisible())
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(TryActivate(currentPath));
    }

    internal static bool ShouldSendSwitch(
        bool peekWindowVisible,
        long lastToggleStartedTicks,
        long nowTicks) =>
        lastToggleStartedTicks != 0
            ? nowTicks - lastToggleStartedTicks < ToggleGracePeriodMs ||
              peekWindowVisible
            : peekWindowVisible;

    /// <summary>
    /// Single activation argument: the preview target path, quoted. File
    /// paths validated by IsPreviewablePath cannot contain quote
    /// characters, so quoting is the complete escaping.
    /// </summary>
    /// <summary>
    /// Quoted single argument for the shell-integrity launcher, which
    /// builds its own command line from a string. Paths validated by
    /// IsPreviewablePath cannot contain quote characters, so quoting is
    /// the complete escaping.
    /// </summary>
    internal static string BuildShellIntegrityArguments(string path) =>
        string.Concat('"', path, '"');

    // ── Availability: running module process + version gate + integrity ──

    internal bool IsAvailable() =>
        ResolveExecutablePath() is not null && !HasBlockingIntegrityMismatch();

    private static string? ResolveExecutablePath()
    {
        if (s_cachedExecutablePath is { } cached)
        {
            return cached;
        }

        string? processPath = null;
        try
        {
            foreach (Process process in Process.GetProcessesByName(PeekProcessName))
            {
                using (process)
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    processPath ??= TryGetProcessPath(process);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception or
                                   NotSupportedException)
        {
            // A process can exit while the snapshot is being inspected.
        }

        string? executablePath = FirstExistingExecutable(
            processPath,
            TryFindRegistryExecutablePath(),
            BuildKnownLocationExecutablePath(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            BuildKnownLocationExecutablePath(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        if (executablePath is null)
        {
            return null;
        }

        // The version gate runs on every negative probe: a pre-0.96
        // install must never receive arguments, whatever path resolved.
        if (!IsVersionSufficient(TryResolveVersion(executablePath)))
        {
            return null;
        }

        s_cachedExecutablePath = executablePath;
        return executablePath;
    }

    internal static bool IsVersionSufficient(string? versionText) =>
        Version.TryParse(versionText?.Trim(), out Version? parsed) &&
        parsed >= MinimumVersion;

    private static string? TryResolveVersion(string executablePath)
    {
        try
        {
            string? fileVersion = FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
            if (!string.IsNullOrWhiteSpace(fileVersion))
            {
                return fileVersion;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or
                                   System.IO.IOException)
        {
            // Fall through to the registry version.
        }

        return TryFindRegistryDisplayVersion();
    }

    // ── Activation ──

    private static bool TryActivate(string path)
    {
        string? executablePath = ResolveExecutablePath();
        if (executablePath is null)
        {
            App.Log("[Peek] Peek unavailable (not running, version below 0.96, or integrity mismatch).");
            return false;
        }

        // Re-verify the module process right before activating: once it
        // quits, an unconditional activation would cold-spawn Peek even
        // when the PowerToys module is disabled — the one way this
        // passive forwarder must never "start" PowerToys.
        if (!IsPeekProcessRunning())
        {
            App.Log("[Peek] Peek module process no longer running; activation skipped.");
            return false;
        }

        return ActivateResolvedExecutable(executablePath, path);
    }

    /// <summary>
    /// Every activation is parented to the Explorer shell's standard-user
    /// integrity via ShellUnelevatedLaunch — the same hand-down the
    /// updater uses. For an elevated DeskBox this avoids spawning an
    /// elevated Peek whose activation redirect into the runner's
    /// standard-integrity instance is unverified cross-integrity
    /// territory (and can strand a second preview window). For an
    /// unelevated DeskBox it inherits the live shell environment, which
    /// is exactly what the Explorer-hosted backend of
    /// Win32Helper.OpenFileOrChooseApp prefers for the same reason. There
    /// is deliberately no bare-spawn fallback: without a shell window
    /// there is no standard-integrity parent to inherit, and this passive
    /// forwarder must not cold-start Peek on its own.
    /// </summary>
    private static bool ActivateResolvedExecutable(string executablePath, string path)
    {
        bool started = ShellUnelevatedLaunch.TryStartAtShellIntegrity(
            executablePath,
            BuildShellIntegrityArguments(path),
            message => App.Log($"[Peek] {message}"));
        if (!started)
        {
            App.Log($"[Peek] Shell-integrity activation failed for '{path}'.");
        }

        return started;
    }

    // ── Process probes ──

    private static bool IsPeekProcessRunning()
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName(PeekProcessName))
            {
                using (process)
                {
                    if (!process.HasExited)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception or
                                   NotSupportedException)
        {
            // A process can exit while the snapshot is being inspected.
        }

        return false;
    }

    /// <summary>
    /// True when any Peek process owns a visible top-level window. Peek
    /// hides (does not destroy) its window when the preview closes while
    /// the module process stays resident, so plain visibility is the
    /// "preview open" signal.
    /// </summary>
    internal static bool IsPeekWindowVisible()
    {
        uint[] processIds = CollectPeekProcessIds();
        if (processIds.Length == 0)
        {
            return false;
        }

        var processIdSet = new HashSet<uint>(processIds);
        bool visible = false;
        _ = Win32Helper.EnumWindows((hWnd, lParam) =>
        {
            _ = Win32Helper.GetWindowThreadProcessId(hWnd, out uint processId);
            if (processIdSet.Contains(processId) &&
                Win32Helper.IsWindowVisible(hWnd))
            {
                visible = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return visible;
    }

    private static uint[] CollectPeekProcessIds()
    {
        List<uint> processIds = [];
        try
        {
            foreach (Process process in Process.GetProcessesByName(PeekProcessName))
            {
                using (process)
                {
                    if (!process.HasExited)
                    {
                        processIds.Add((uint)process.Id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception or
                                   NotSupportedException)
        {
            // A process can exit while the snapshot is being inspected.
        }

        return [.. processIds];
    }

    // ── Integrity mismatch detection & one-time hint ──

    private static bool HasBlockingIntegrityMismatch()
    {
        if (!IsAnyPeekProcessElevated() ||
            ShellUnelevatedLaunch.IsCurrentProcessElevated())
        {
            return false;
        }

        if (!s_integrityHintLogged)
        {
            s_integrityHintLogged = true;
            App.Log("[Peek] Integrity mismatch detected: PowerToys Peek is running as administrator " +
                    "but DeskBox is not. Peek previews stay disabled to avoid spawning a second, " +
                    "unelevated Peek window next to the elevated one. " +
                    "To fix: run PowerToys as a normal user (not as administrator).");
        }

        return true;
    }

    private static bool IsAnyPeekProcessElevated()
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName(PeekProcessName))
            {
                using (process)
                {
                    if (!process.HasExited &&
                        TryGetProcessElevation(process.Id, out bool elevated) &&
                        elevated)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception or
                                   NotSupportedException)
        {
            // Best-effort check — ignore failures.
        }

        return false;
    }

    // ── Path resolution helpers ──

    private static string? FirstExistingExecutable(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                string.Equals(
                    Path.GetFileName(candidate),
                    PeekExecutableName,
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            // Elevated or store-installed copies can deny module queries.
            return null;
        }
    }

    private static string? BuildKnownLocationExecutablePath(string installRoot) =>
        string.IsNullOrWhiteSpace(installRoot)
            ? null
            : Path.Combine(installRoot, "PowerToys", WinUi3AppsFolder, PeekExecutableName);

    /// <summary>
    /// Traditional uninstall keys miss the store (MSIX) install of
    /// PowerToys; they are only the fallback after the running process's
    /// own image path.
    /// </summary>
    private static (string? InstallLocation, string? DisplayVersion)? TryFindPowerToysUninstallEntry()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? uninstall = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (string subKeyName in uninstall.GetSubKeyNames())
                    {
                        using RegistryKey? appKey = uninstall.OpenSubKey(subKeyName);
                        if (appKey?.GetValue("DisplayName") is not string displayName ||
                            displayName.IndexOf("PowerToys", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        string? installLocation = appKey.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(installLocation))
                        {
                            continue;
                        }

                        return (installLocation, appKey.GetValue("DisplayVersion") as string);
                    }
                }
                catch
                {
                    // Missing or inaccessible registry views are normal.
                }
            }
        }

        return null;
    }

    private static string? TryFindRegistryExecutablePath() =>
        TryFindPowerToysUninstallEntry()?.InstallLocation is { } installLocation
            ? Path.Combine(installLocation, WinUi3AppsFolder, PeekExecutableName)
            : null;

    private static string? TryFindRegistryDisplayVersion() =>
        TryFindPowerToysUninstallEntry()?.DisplayVersion;

    // ── Win32 interop (elevation probe only) ──

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformationClass = 20;

    private static bool TryGetProcessElevation(int processId, out bool elevated)
    {
        elevated = false;
        nint processHandle = Kernel32NativeMethods.OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        if (processHandle == 0)
        {
            return false;
        }

        try
        {
            if (!AdvApi32NativeMethods.OpenProcessToken(
                    processHandle,
                    TokenQuery,
                    out nint tokenHandle))
            {
                return false;
            }

            try
            {
                if (!AdvApi32NativeMethods.GetTokenInformation(
                        tokenHandle,
                        TokenElevationInformationClass,
                        out AdvApi32NativeMethods.TokenElevation elevation,
                        System.Runtime.InteropServices.Marshal.SizeOf<AdvApi32NativeMethods.TokenElevation>(),
                        out _))
                {
                    return false;
                }

                elevated = elevation.TokenIsElevated != 0;
                return true;
            }
            finally
            {
                _ = Kernel32NativeMethods.CloseHandle(tokenHandle);
            }
        }
        finally
        {
            _ = Kernel32NativeMethods.CloseHandle(processHandle);
        }
    }
}
