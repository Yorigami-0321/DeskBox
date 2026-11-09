using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class GlobalHotkeySafetyContractTests
{
    [Fact]
    public void SettingsExposeExplicitPresetsAndPersistentSystemOverrideWarning()
    {
        string xaml = Read("src/DeskBox/Views/SettingsWindow.xaml");
        string hotkeyCode = Read("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs");
        string dialogCode = Read("src/DeskBox/Views/Dialogs/HotkeyRecorderDialog.xaml.cs");
        string dialogXaml = Read("src/DeskBox/Views/Dialogs/HotkeyRecorderDialog.xaml");

        Assert.Contains("x:Name=\"GlobalHotkeyCaptureButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyCustomRow\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DesktopDoubleClickToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalHotkeyReservedWarning\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanShowHotkeyWarning", xaml, StringComparison.Ordinal);

        // The preset list lives inside the shared recorder dialog now: all six
        // presets ride through the dialog's preset panel.
        Assert.Contains("Settings.GlobalHotkey.Preset.F7", hotkeyCode, StringComparison.Ordinal);
        Assert.Contains("Settings.GlobalHotkey.Preset.DoubleControl", hotkeyCode, StringComparison.Ordinal);
        Assert.Contains("Settings.GlobalHotkey.Preset.AltSpace", hotkeyCode, StringComparison.Ordinal);
        Assert.Contains("Settings.GlobalHotkey.Preset.WinSpace", hotkeyCode, StringComparison.Ordinal);
        Assert.Contains("Settings.GlobalHotkey.Preset.WindowsTap", hotkeyCode, StringComparison.Ordinal);
        Assert.Contains("Settings.GlobalHotkey.Preset.CopilotKey", hotkeyCode, StringComparison.Ordinal);
        Assert.Contains("Settings.GlobalHotkey.PresetsTitle", dialogCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.GlobalHotkey.PresetsDescription", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.GlobalHotkey.RecommendedTitle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.GlobalHotkey.SystemTitle", xaml, StringComparison.Ordinal);

        // Recording rides the dedicated low-level hook dialog: registrations
        // suspend while it is open and restore on close. The dialog drives
        // the suspension through the injected hotkey facade; the in-process
        // facade implementation is what touches the two live services.
        Assert.Contains("SuspendHotkeysForRecording", dialogCode, StringComparison.Ordinal);
        Assert.Contains("ResumeHotkeysAfterRecording", dialogCode, StringComparison.Ordinal);
        string facadeCode = Read("src/DeskBox/Services/InProcessSettingsHotkeyActions.cs");
        Assert.Contains("SuspendForRecording", facadeCode, StringComparison.Ordinal);
        Assert.Contains("ResumeAfterRecording", facadeCode, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ErrorInfoBar\"", dialogXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWinSpaceHotkeyButton_Click", hotkeyCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CopilotKeyPreset_StaysAPlainChordWithAFriendlyDisplayName()
    {
        string serviceCode = Read("src/DeskBox/Services/GlobalHotkeyService.cs");
        string hotkeyCode = Read("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs");

        Assert.Contains(
            "gesture.Equals(CopilotKeyGesture)",
            Slice(serviceCode, "public static string FormatGesture", "private IntPtr WindowSubclassProc"),
            StringComparison.Ordinal);
        Assert.Contains(
            "GlobalHotkeyActivation.FromChord(GlobalHotkeyService.CopilotKeyGesture)",
            hotkeyCode,
            StringComparison.Ordinal);
        Assert.Contains("Settings.GlobalHotkey.Preset.CopilotKey", hotkeyCode, StringComparison.Ordinal);

        // The Copilot preset must keep riding the standard RegisterHotKey path:
        // a reserved-hook mode would make it depend on the low-level hook.
        string hookModes = Slice(
            serviceCode,
            "private static bool TryGetReservedHookMode",
            "private static uint ToWin32Modifiers");
        Assert.DoesNotContain("Copilot", hookModes, StringComparison.Ordinal);
    }

    [Fact]
    public void Recorder_IgnoresReservedHookMaskKeyAndInjectedInput()
    {
        Assert.True(ReservedHotkeyHookService.IsInternalMaskKey(0xE8));
        Assert.False(ReservedHotkeyHookService.IsInternalMaskKey(0x20));

        // The recording session never turns the reserved hook's synthetic mask
        // key into a captured chord.
        var session = new HotkeyRecordingSession();
        session.ProcessKey(0xE8, isKeyDown: true);
        Assert.False(session.HasGesture);

        // Injected events are dropped inside the capture hook itself.
        string hook = Read("src/DeskBox/Services/HotkeyRecorderHookService.cs");
        Assert.Contains("LLKHF_INJECTED", hook, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationChange_UsesTheRealRegistrationAsCommitPointAndRestoresPreviousActivation()
    {
        string source = Read("src/DeskBox/Services/GlobalHotkeyService.cs");
        string apply = Slice(
            source,
            "public bool TryApplyActivation",
            "public void SetEnabled");

        Assert.DoesNotContain("CanRegister", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeHotkeyId", source, StringComparison.Ordinal);
        Assert.Contains("HotkeyActivationKind previousKind", apply, StringComparison.Ordinal);
        Assert.Contains("int previousModifiers", apply, StringComparison.Ordinal);
        Assert.Contains("int previousVirtualKey", apply, StringComparison.Ordinal);
        Assert.Contains("RefreshRegistration();", apply, StringComparison.Ordinal);
        Assert.Contains("settings.GlobalHotkeyActivationKind = previousKind", apply, StringComparison.Ordinal);
        Assert.Contains("settings.GlobalHotkeyModifiers = previousModifiers", apply, StringComparison.Ordinal);
        Assert.Contains("settings.GlobalHotkeyKey = previousVirtualKey", apply, StringComparison.Ordinal);
        Assert.Contains("if (IsRegistered)", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void HotkeyServices_ExposeReceiveInvokeAndDispatchFailureCounters()
    {
        string global = Read("src/DeskBox/Services/GlobalHotkeyService.cs");

        Assert.Contains("public long ReceivedCount", global, StringComparison.Ordinal);
        Assert.Contains("public long InvocationCount", global, StringComparison.Ordinal);
        Assert.Contains("public long DispatchFailureCount", global, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref _receivedSequence)", global, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref _invocationSequence)", global, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref _dispatchFailureSequence)", global, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservedHook_IgnoresInjectedInputAndFailsOpenWhenDeliveryFails()
    {
        string hook = Read("src/DeskBox/Services/ReservedHotkeyHookService.cs");
        string win32 = Read("src/DeskBox/Platform/Win32Helper.cs");

        Assert.Contains("LLKHF_INJECTED", hook, StringComparison.Ordinal);
        Assert.Contains("TaskCompletionSource", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("ManualResetEventSlim", hook, StringComparison.Ordinal);
        Assert.Contains("_lifecycleGeneration", hook, StringComparison.Ordinal);
        Assert.Contains("generation != _lifecycleGeneration", hook, StringComparison.Ordinal);
        Assert.Contains("TrySendTaggedKeyPress", hook, StringComparison.Ordinal);
        Assert.Contains("ReservedHotkeyMode.DoubleControl", hook, StringComparison.Ordinal);
        Assert.Contains("ReservedHotkeyMode.WindowsTap", hook, StringComparison.Ordinal);
        Assert.Contains("TriggerAndPassThrough", hook, StringComparison.Ordinal);
        Assert.Contains("CancelSuppression();", hook, StringComparison.Ordinal);
        Assert.Contains("PostMessage", hook, StringComparison.Ordinal);
        Assert.Contains("SendInput", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("keybd_event", win32, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleRecovery_ReRegistersGlobalHotkeyAfterExternalSessionChanges()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string recovery = Slice(
            app,
            "private void OnLifecycleRecoveryRequested",
            "private void FlushSettingsForEndSession");

        Assert.Contains("GlobalHotkeyService?.RefreshRegistration();", recovery, StringComparison.Ordinal);
        Assert.Contains("DesktopDoubleClickActivationService?.RefreshRegistration();", recovery, StringComparison.Ordinal);
        Assert.Contains("requiresExternalRecovery", recovery, StringComparison.Ordinal);
    }

    private static string Read(string path)
    {
        return File.ReadAllText(TestPaths.FromRepository(path));
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
