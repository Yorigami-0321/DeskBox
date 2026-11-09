#if DESKBOX_NATIVE_AOT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Windows.System;
using WinRT.Interop;

namespace DeskBox;

public partial class App
{
    private const string AotHotkeySmokeEnvironmentVariable =
        "DESKBOX_AOT_HOTKEY_SMOKE";
    private const string AotHotkeyPhaseEnvironmentVariable =
        "DESKBOX_AOT_HOTKEY_PHASE";
    private const string AotHotkeyRunIdEnvironmentVariable =
        "DESKBOX_AOT_HOTKEY_RUN_ID";
    private const string AotHotkeySmokeScenario = "RegistrationLifecycle";
    private const string AotHotkeyPrimaryPhase = "Primary";
    private const string AotHotkeyReleasePhase = "Release";
    private const string AotHotkeySmokeDirectoryName = "aot-hotkey-smoke";
    private const int AotGlobalConflictHotkeyId = 0x4452;
    private const uint AotModAlt = 0x0001;
    private const uint AotModControl = 0x0002;
    private const uint AotModShift = 0x0004;
    private const uint AotModNoRepeat = 0x4000;
    private static readonly IntPtr AotHotkeyInjectedEventTag = new(0x4442484B);

    private void StartAotHotkeySmokeIfRequested()
    {
        string? scenario = Environment.GetEnvironmentVariable(
            AotHotkeySmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(scenario))
        {
            return;
        }

        string? phase = Environment.GetEnvironmentVariable(
            AotHotkeyPhaseEnvironmentVariable);
        string? runId = Environment.GetEnvironmentVariable(
            AotHotkeyRunIdEnvironmentVariable);
        if (!string.Equals(
                scenario.Trim(),
                AotHotkeySmokeScenario,
                StringComparison.Ordinal) ||
            phase is not AotHotkeyPrimaryPhase and not AotHotkeyReleasePhase ||
            !Guid.TryParseExact(runId, "N", out _))
        {
            Log(
                $"[AotHotkeySmoke] Refused unsupported request scenario='{scenario}' " +
                $"phase='{phase}' runId='{runId}'.");
            return;
        }

        _ = RunAotHotkeySmokeAsync(phase, runId!);
    }

    private async Task RunAotHotkeySmokeAsync(string phase, string runId)
    {
        await Task.Yield();

        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !AotHotkeyPathsEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            Log(
                "[AotHotkeySmoke] RefusedNonPreviewRoot: the hotkey matrix " +
                "requires an explicit isolated Native AOT preview root.");
            return;
        }

        string smokeRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotHotkeySmokeDirectoryName));
        string phaseRoot = Path.GetFullPath(Path.Combine(
            smokeRoot,
            phase.ToLowerInvariant()));
        if (!AotHotkeyIsPathEqualOrInside(dataPaths.RootPath, smokeRoot) ||
            !AotHotkeyIsPathEqualOrInside(smokeRoot, phaseRoot))
        {
            Log($"[AotHotkeySmoke] Refused unsafe result root '{phaseRoot}'.");
            return;
        }

        Directory.CreateDirectory(phaseRoot);
        string resultPath = Path.Combine(phaseRoot, "result.json");
        var result = new AotHotkeySmokeResult
        {
            SchemaVersion = 1,
            Stage = "5B-4C2A",
            Scenario = AotHotkeySmokeScenario,
            Phase = phase,
            RunId = runId,
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath ?? string.Empty,
            PreviewDataRoot = dataPaths.RootPath,
            ResultPath = resultPath,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            InputSource = "SyntheticSendInputForRegisterHotKeyOnly",
            PhysicalStandardKeyboardVerified = false,
            PhysicalWinSpaceVerified = false,
            PhysicalRecorderVerified = false,
            Steps = []
        };
        WriteAotHotkeyResult(resultPath, result);

        try
        {
            await CaptureAotHotkeyRegistrationLifecycleAsync(result);
            result.ExecutableSha256 = ComputeAotHotkeySha256(result.ExecutablePath);
            RequireAotHotkey(
                result,
                !result.IsDynamicCodeSupported,
                "runtime-native-aot",
                "Hotkey smoke did not run inside Native AOT.");
            result.Success = true;
            result.State = "Completed";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.State = "Failed";
            result.Error = ex.ToString();
            Log($"[AotHotkeySmoke] Phase {phase} failed: {ex}");
        }
        finally
        {
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            result.NormalShutdownRequested = true;
            WriteAotHotkeyResult(resultPath, result);
            Log(
                $"[AotHotkeySmoke] phase={phase} state={result.State} " +
                $"success={result.Success} result='{resultPath}'");
            await Task.Delay(100);
            await ShutdownApplicationAsync();
        }
    }

    private async Task CaptureAotHotkeyRegistrationLifecycleAsync(
        AotHotkeySmokeResult result)
    {
        RequireAotHotkey(
            result,
            _trayWindow is not null && GlobalHotkeyService is not null,
            "services-ready",
            "Tray window or global hotkey service is unavailable.");

        IntPtr trayWindowHandle = WindowNative.GetWindowHandle(_trayWindow!);
        result.TrayWindowHandle = $"0x{trayWindowHandle.ToInt64():X}";
        RequireAotHotkey(
            result,
            trayWindowHandle != IntPtr.Zero && Win32Helper.IsWindow(trayWindowHandle),
            "tray-hwnd-valid",
            "The hotkey host HWND is invalid.");

        var settings = SettingsService.Settings;
        GlobalHotkeyService global = GlobalHotkeyService!;
        result.StartupGlobalEnabled = settings.GlobalHotkeyEnabled;
        result.StartupGlobalRegistered = global.IsRegistered;
        if (result.Phase == AotHotkeyPrimaryPhase)
        {
            RequireAotHotkey(
                result,
                !result.StartupGlobalEnabled && !result.StartupGlobalRegistered,
                "primary-startup-disabled",
                "Primary phase must start with the hotkey disabled.");
        }
        else
        {
            RequireAotHotkey(
                result,
                result.StartupGlobalEnabled && result.StartupGlobalRegistered,
                "release-startup-reregistered",
                "Release phase did not re-register the gesture after prior shutdown.");
        }

        global.SetEnabled(false);
        RequireAotHotkey(
            result,
            !global.IsRegistered,
            "disabled-unregistered",
            "Disabling did not unregister the hotkey.");

        var globalGesture = new GlobalHotkeyGesture(
            HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift,
            (int)VirtualKey.F23);
        bool globalApplied = global.TryApplyGesture(globalGesture, out string? globalError);
        result.GlobalStandardApplySucceeded = globalApplied;
        result.GlobalStandardApplyError = globalError;
        global.SetEnabled(true);
        result.GlobalStandardRegistered = global.IsRegistered;
        result.GlobalStandardUsesReservedHook = global.UsesReservedHook;
        RequireAotHotkey(
            result,
            globalApplied && global.IsRegistered && !global.UsesReservedHook &&
            global.CurrentGesture.Equals(globalGesture),
            "global-standard-registered",
            $"Global standard registration failed: {globalError}");

        await TriggerGlobalStandardHotkeyAsync(global, result);
        ExerciseGlobalConflictRollback(trayWindowHandle, global, globalGesture, result);
        await ExerciseReservedHookLifecycleAsync(global, globalGesture, result);

        global.SetEnabled(false);
        RequireAotHotkey(
            result,
            !global.IsRegistered,
            "final-disable-unregistered",
            "Final disable did not unregister the hotkey.");
        global.SetEnabled(true);
        result.FinalGlobalRegistered = global.IsRegistered;
        RequireAotHotkey(
            result,
            result.FinalGlobalRegistered && !global.UsesReservedHook,
            "final-reregistered",
            "Final re-registration failed.");
    }

    private static async Task TriggerGlobalStandardHotkeyAsync(
        GlobalHotkeyService service,
        AotHotkeySmokeResult result)
    {
        long receivedBefore = service.ReceivedCount;
        long invokedBefore = service.InvocationCount;
        long dispatchFailuresBefore = service.DispatchFailureCount;
        ushort[] modifiers = [0x11, 0x10]; // Ctrl + Shift
        result.GlobalSyntheticInputSent = Win32Helper.TrySendTaggedKeyChord(
            modifiers,
            (ushort)VirtualKey.F23,
            AotHotkeyInjectedEventTag,
            out int inputError);
        result.GlobalSyntheticInputError = inputError;
        RequireAotHotkey(
            result,
            result.GlobalSyntheticInputSent,
            "global-synthetic-input-sent",
            $"Global test chord SendInput failed with {inputError}.");
        bool invoked = await WaitForAotHotkeyConditionAsync(
            () => service.InvocationCount > invokedBefore,
            TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        result.GlobalReceivedDelta = service.ReceivedCount - receivedBefore;
        result.GlobalInvocationDelta = service.InvocationCount - invokedBefore;
        result.GlobalDispatchFailureDelta =
            service.DispatchFailureCount - dispatchFailuresBefore;
        RequireAotHotkey(
            result,
            invoked && result.GlobalReceivedDelta == 1 &&
            result.GlobalInvocationDelta == 1 &&
            result.GlobalDispatchFailureDelta == 0,
            "global-registerhotkey-dispatched",
            "Global RegisterHotKey did not dispatch exactly once.");
    }

    private static void ExerciseGlobalConflictRollback(
        IntPtr trayWindowHandle,
        GlobalHotkeyService service,
        GlobalHotkeyGesture expectedGesture,
        AotHotkeySmokeResult result)
    {
        bool holderRegistered = Win32Helper.RegisterHotKey(
            trayWindowHandle,
            AotGlobalConflictHotkeyId,
            AotModControl | AotModAlt | AotModNoRepeat,
            (uint)VirtualKey.F22);
        result.GlobalConflictHolderRegistered = holderRegistered;
        result.GlobalConflictHolderError = holderRegistered ? 0 : Marshal.GetLastWin32Error();
        RequireAotHotkey(
            result,
            holderRegistered,
            "global-conflict-holder-registered",
            $"Global conflict holder failed with {result.GlobalConflictHolderError}.");
        try
        {
            bool applied = service.TryApplyGesture(
                new GlobalHotkeyGesture(
                    HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt,
                    (int)VirtualKey.F22),
                out string? error);
            result.GlobalConflictApplyReturned = applied;
            result.GlobalConflictApplyError = error;
            result.GlobalConflictRolledBack = !applied && service.IsRegistered &&
                service.CurrentGesture.Equals(expectedGesture);
            RequireAotHotkey(
                result,
                result.GlobalConflictRolledBack,
                "global-conflict-rolled-back",
                "Global conflict did not restore the previous registration.");
        }
        finally
        {
            result.GlobalConflictHolderReleased = Win32Helper.UnregisterHotKey(
                trayWindowHandle,
                AotGlobalConflictHotkeyId);
        }
    }

    private static async Task ExerciseReservedHookLifecycleAsync(
        GlobalHotkeyService service,
        GlobalHotkeyGesture standardGesture,
        AotHotkeySmokeResult result)
    {
        long receivedBefore = service.ReceivedCount;
        long invokedBefore = service.InvocationCount;
        bool applied = service.TryApplyGesture(
            new GlobalHotkeyGesture(
                HotkeyModifierKeys.Windows,
                (int)VirtualKey.Space),
            out string? error);
        result.ReservedHookApplySucceeded = applied;
        result.ReservedHookApplyError = error;
        result.ReservedHookRegistered = service.IsRegistered;
        result.ReservedHookUsesHook = service.UsesReservedHook;
        result.ReservedHookThreadId = service.ReservedHookThreadId;
        result.ReservedHookLastError = service.ReservedHookLastErrorCode;
        result.ReservedHookSyntheticTriggerAttempted = false;
        RequireAotHotkey(
            result,
            applied && service.IsRegistered && service.UsesReservedHook &&
            service.ReservedHookThreadId != 0 && service.ReservedHookLastErrorCode == 0,
            "reserved-hook-installed",
            $"Reserved hook startup failed: {error}");

        await Task.Delay(150);
        result.ReservedHookReceivedDeltaWithoutInput =
            service.ReceivedCount - receivedBefore;
        result.ReservedHookInvocationDeltaWithoutInput =
            service.InvocationCount - invokedBefore;
        RequireAotHotkey(
            result,
            result.ReservedHookReceivedDeltaWithoutInput == 0 &&
            result.ReservedHookInvocationDeltaWithoutInput == 0,
            "reserved-hook-no-synthetic-claim",
            "Reserved hook triggered without a physical input sample.");

        bool restored = service.TryApplyGesture(standardGesture, out string? restoreError);
        bool stopped = await WaitForAotHotkeyConditionAsync(
            () => service.ReservedHookThreadId == 0,
            TimeSpan.FromSeconds(3));
        result.ReservedHookRestoreSucceeded = restored;
        result.ReservedHookRestoreError = restoreError;
        result.ReservedHookStopped = stopped && !service.UsesReservedHook &&
            service.IsRegistered && service.CurrentGesture.Equals(standardGesture);
        RequireAotHotkey(
            result,
            result.ReservedHookStopped,
            "reserved-hook-stopped-and-standard-restored",
            $"Reserved hook teardown or standard restore failed: {restoreError}");
    }

    private static async Task<bool> WaitForAotHotkeyConditionAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return predicate();
    }

    private static void RequireAotHotkey(
        AotHotkeySmokeResult result,
        bool condition,
        string step,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }

        result.Steps.Add(step);
    }

    private static void WriteAotHotkeyResult(
        string resultPath,
        AotHotkeySmokeResult result)
    {
        string temporaryPath = resultPath + ".tmp";
        string json = JsonSerializer.Serialize(
            result,
            AotHotkeySmokeJsonContext.Default.AotHotkeySmokeResult);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, resultPath, overwrite: true);
    }

    private static string ComputeAotHotkeySha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool AotHotkeyPathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool AotHotkeyIsPathEqualOrInside(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return AotHotkeyPathsEqual(normalizedRoot, normalizedCandidate) ||
            normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class AotHotkeySmokeResult
{
    public int SchemaVersion { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool Success { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int ProcessId { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;
    public string ExecutableSha256 { get; set; } = string.Empty;
    public string PreviewDataRoot { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public string TrayWindowHandle { get; set; } = string.Empty;
    public bool IsDynamicCodeSupported { get; set; }
    public string InputSource { get; set; } = string.Empty;
    public bool PhysicalStandardKeyboardVerified { get; set; }
    public bool PhysicalWinSpaceVerified { get; set; }
    public bool PhysicalRecorderVerified { get; set; }
    public bool StartupGlobalEnabled { get; set; }
    public bool StartupGlobalRegistered { get; set; }
    public bool GlobalStandardApplySucceeded { get; set; }
    public string? GlobalStandardApplyError { get; set; }
    public bool GlobalStandardRegistered { get; set; }
    public bool GlobalStandardUsesReservedHook { get; set; }
    public bool GlobalSyntheticInputSent { get; set; }
    public int GlobalSyntheticInputError { get; set; }
    public long GlobalReceivedDelta { get; set; }
    public long GlobalInvocationDelta { get; set; }
    public long GlobalDispatchFailureDelta { get; set; }
    public bool GlobalConflictHolderRegistered { get; set; }
    public int GlobalConflictHolderError { get; set; }
    public bool GlobalConflictApplyReturned { get; set; }
    public string? GlobalConflictApplyError { get; set; }
    public bool GlobalConflictRolledBack { get; set; }
    public bool GlobalConflictHolderReleased { get; set; }
    public bool ReservedHookApplySucceeded { get; set; }
    public string? ReservedHookApplyError { get; set; }
    public bool ReservedHookRegistered { get; set; }
    public bool ReservedHookUsesHook { get; set; }
    public uint ReservedHookThreadId { get; set; }
    public int ReservedHookLastError { get; set; }
    public bool ReservedHookSyntheticTriggerAttempted { get; set; }
    public long ReservedHookReceivedDeltaWithoutInput { get; set; }
    public long ReservedHookInvocationDeltaWithoutInput { get; set; }
    public bool ReservedHookRestoreSucceeded { get; set; }
    public string? ReservedHookRestoreError { get; set; }
    public bool ReservedHookStopped { get; set; }
    public bool FinalGlobalRegistered { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public List<string> Steps { get; set; } = [];
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotHotkeySmokeResult),
    TypeInfoPropertyName = "AotHotkeySmokeResult")]
internal partial class AotHotkeySmokeJsonContext : JsonSerializerContext
{
}
#endif
