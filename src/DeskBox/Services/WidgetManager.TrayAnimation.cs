﻿// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.Platform;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Diagnostics;

namespace DeskBox.Services;

/// <summary>
/// Partial class containing TrayAnimation logic for WidgetManager.
/// </summary>
public sealed partial class WidgetManager
{

    private const double OffscreenAnimationPadding = 16.0;
    private long _trayRaiseBatchGeneration;

    // Single shared driver for batch tray animations: one clock and one
    // atomic DeferWindowPos commit per frame, so all windows slide in
    // lockstep instead of staggering per-window (the "wave" effect).
    private readonly WidgetTrayBatchAnimationDriver _trayBatchAnimationDriver = new(App.LogVerbose);

    /// <summary>
    /// Bring desktop widgets to the front of the normal Z-order from the tray.
    /// </summary>
    public Task<bool?> RaiseWidgetsFromTrayAsync()
    {
        return RunOnUiThreadAsync(async () =>
        {
            bool? result = null;
            await ExecuteTrayVisibilityOperationAsync(
                "raise-from-tray",
                async () => result = await RaiseWidgetsFromTrayCoreAsync());
            return result;
        });
    }

    private async Task<bool?> RaiseWidgetsFromTrayCoreAsync(
        string source = "raise-from-tray")
    {
        using var perfScope = PerformanceLogger.Measure("WidgetManager.RaiseWidgetsFromTray");
        // A user reveal ends any open removal grace (spec 5.6): the parked
        // topology restore applies before the widgets are shown.
        EndRemovalGraceByUserAction?.Invoke();
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            App.LogVerbose("[TrayBatch] Raise redirected to desktop-pinned show");
            await SetAllWidgetsVisibleCoreAsync(true);
            return false;
        }

        var now = DateTime.UtcNow;
        bool preserveSourceForeground =
            WidgetLayerService.UsesQuickRevealMode() &&
            string.Equals(source, "desktop-double-click", StringComparison.Ordinal);
        IntPtr sourceForeground = Win32Helper.GetForegroundWindow();
        double sinceLastToggleMs = (now - _lastTrayLayerToggleUtc).TotalMilliseconds;
        App.LogVerbose(
            $"[TrayBatch] Raise requested source={source} preserveSourceForeground={preserveSourceForeground} " +
            $"raised={_widgetsRaisedFromTray} toggling={_isTogglingWidgetsDesktopLayer} " +
            $"sinceLastMs={sinceLastToggleMs:F0} loadedFile={_fileWidgets.Count} loadedContent={_contentWidgets.Count}");
        // ⭐ 移除 320ms 节流限制，确保即时响应
        if (_isTogglingWidgetsDesktopLayer)
        {
            App.LogVerbose("[TrayBatch] Raise ignored reason=busy");
            return null;
        }

        _isTogglingWidgetsDesktopLayer = true;
        _lastTrayLayerToggleUtc = now;
        _lastRaiseOriginatedFromTrayIcon =
            string.Equals(source, "tray-icon", StringComparison.Ordinal);
        // A tray raise (tray icon, hotkey, desktop double-click, Quick Reveal)
        // is the first explicit user reveal of a hidden startup session.
        ClearStartupHiddenSession(source);
        using var hydrationPause = WidgetIconHydrationGate.Pause();
        try
        {
            CancelActiveTrayAnimationsAndRestorePositions();
            var candidates = _settingsService.Settings.Widgets
                .Where(IsSessionCandidate)
                .ToList();
            App.LogVerbose($"[TrayBatch] Raise candidates={candidates.Count} widgets={FormatWidgetList(candidates)}");

            var windowsToRaise = new List<IDesktopWidgetWindow>();
            foreach (var widget in candidates)
            {
                try
                {
                    var window = await PrepareWidgetForBatchShowAsync(widget, showRaisedWhileInitializing: true);
                    if (window is null)
                    {
                        continue;
                    }

                    windowsToRaise.Add(window);
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to prepare widget for tray raise '{widget.Name}' ({widget.Id}): {ex}");
                }
            }

            App.LogVerbose($"[TrayBatch] Raise prepared={windowsToRaise.Count}/{candidates.Count}");
            var windowsToAnimate = windowsToRaise
                .Where(window => !window.Visible)
                .ToList();
            PrepareTrayShowAnimations(windowsToAnimate);

            _widgetsRaisedFromTray = windowsToRaise.Count > 0;
            var shownWindows = new List<IDesktopWidgetWindow>();
            foreach (var window in windowsToRaise)
            {
                try
                {
                    if (window.Visible)
                    {
                        window.EnsureRaisedFromTrayTopMost();
                    }
                    else
                    {
                        window.ShowPreparedRaisedFromTray(persistVisibility: false);
                    }

                    shownWindows.Add(window);
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to show prepared widget from tray {FormatHostWindow(window)}: {ex}");
                }
            }

            // Keep the foreground that originated the request. Capturing after
            // showing the windows observes DeskBox's own temporary activation
            // instead of the desktop/application the user actually invoked it from.
            _foregroundAtRaiseTime = sourceForeground;
            _suppressTrayLayerRestoreUntilUtc = DateTime.UtcNow.AddMilliseconds(160);
            PlayPreparedTrayShowAnimations(windowsToAnimate);
            SetWidgetsRaisedFromTray(shownWindows.Count > 0);
            // Release the raised group once the foreground leaves DeskBox (e.g. the
            // user clicks another app window). Without the monitor the widgets stay
            // topmost until the next toggle, covering whatever the user clicks.
            StartTrayLayerRestoreMonitor(
                shownWindows.Count > 0,
                preserveSourceForeground);
            if (WidgetLayerService.UsesQuickRevealMode())
            {
                // Activation pulses TOPMOST back into the normal band for the
                // ordinary dynamic layer. In quick-reveal mode, activate first
                // and then establish the persistent, non-activating overlay.
                // A desktop double-click is still being delivered to Explorer;
                // activating DeskBox here makes that same click look like a later
                // deskbox-leave transition and immediately dismisses the reveal.
                if (!preserveSourceForeground)
                {
                    ActivateIdleHighestWindow(shownWindows);
                }

                QueueTrayRaiseTopMostConfirmation(shownWindows);
            }
            else
            {
                // The HWND_TOP group lift cannot cross the foreground window
                // while DeskBox is still a background process; activating the
                // idle-highest widget first gives the lift a foreground owner
                // so every raised window lands above the application the user
                // invoked from (first-raise-after-idle otherwise shows only
                // the activated widget).
                ActivateIdleHighestWindow(shownWindows);
                QueueTrayRaiseTopMostConfirmation(shownWindows);
            }
            SaveBatchVisibilityState();
            await _trayBatchAnimationDriver.WaitForIdleAsync();
            if (WidgetLayerService.UsesQuickRevealMode() &&
                shownWindows.Count > 0)
            {
                // Desktop double-click deliberately does not activate (the
                // click is still owned by Explorer), so the first TOPMOST
                // transaction can race the reveal/uncloak and land below a
                // foreign window. Re-assert after the show settles.
                QueueQuickRevealTopMostRecheck(shownWindows);
            }
            if (!WidgetLayerService.UsesQuickRevealMode() &&
                shownWindows.Count > 0)
            {
                // The one-shot band recheck must observe the settled band:
                // queued from the confirm itself, the delayed callback always
                // landed inside this raise's own toggle window (animations
                // run 137ms+ against an 80ms delay) and aborted on the
                // toggling guard — the guard's only effect was blocking the
                // raise that queued it. After the idle await the toggle
                // window has closed; a new raise or hide still aborts the
                // callback through the generation and session guards.
                QueueTrayRaiseBandRecheck(
                    shownWindows,
                    _trayRaiseBatchGeneration);
            }

            App.LogVerbose($"[TrayBatch] Raise completed raised={_widgetsRaisedFromTray} prepared={windowsToRaise.Count} shown={shownWindows.Count} animated={windowsToAnimate.Count}");
            return _widgetsRaisedFromTray;
        }
        finally
        {
            _isTogglingWidgetsDesktopLayer = false;
        }
    }

    private async Task<IDesktopWidgetWindow?> PrepareWidgetForBatchShowAsync(
        WidgetConfig config,
        bool showRaisedWhileInitializing = false)
    {
        if (IsDeleted(config.Id))
        {
            App.LogVerbose($"[TrayBatch] Prepare skipped reason=deleted widget={FormatWidget(config)}");
            return null;
        }

        if (config.IsDisabled)
        {
            App.LogVerbose($"[TrayBatch] Prepare skipped reason=disabled widget={FormatWidget(config)}");
            return null;
        }

        if (config.WidgetKind != WidgetKind.File)
        {
            if (IsContentFeatureWidgetKind(config.WidgetKind))
            {
                if (!GetFeatureWidgetEnabledState(config.WidgetKind))
                {
                    App.LogVerbose($"[TrayBatch] Prepare skipped reason=feature-disabled widget={FormatWidget(config)}");
                    return null;
                }

                if (_contentWidgets.TryGetValue(config.Id, out var existingContent))
                {
                    App.LogVerbose($"[TrayBatch] Prepare useLoaded content widget={FormatWidget(config)} {FormatHostWindow(existingContent)}");
                    existingContent.RestoreBoundsForCurrentTopology();
                    if (!existingContent.Visible)
                    {
                        existingContent.PrepareTrayShowAnimation();
                    }

                    await WaitForInitialContentReadyAsync(existingContent);
                    return existingContent;
                }

                App.LogVerbose($"[TrayBatch] Prepare createContent widget={FormatWidget(config)} raisedInit={showRaisedWhileInitializing}");
                var createdContent = await CreateRegisteredWidgetFromConfigAsync(
                    config,
                    keepPreparedForAnimation: true,
                    showRaisedWhileInitializing: showRaisedWhileInitializing);
                if (createdContent is not null)
                {
                    await WaitForInitialContentReadyAsync(createdContent);
                }

                return createdContent;
            }

            App.LogVerbose($"[TrayBatch] Prepare skipped reason=unsupported-kind widget={FormatWidget(config)}");
            return null;
        }

        if (GetLoadedWindow(config.Id) is { } existing)
        {
            App.LogVerbose($"[TrayBatch] Prepare useLoaded widget={FormatWidget(config)} {FormatHostWindow(existing)}");
            existing.RestoreBoundsForCurrentTopology();
            if (!existing.Visible)
            {
                existing.PrepareTrayShowAnimation();
            }

            await WaitForInitialContentReadyAsync(existing);
            return existing;
        }

        App.LogVerbose($"[TrayBatch] Prepare createFile widget={FormatWidget(config)} raisedInit={showRaisedWhileInitializing}");
        var window = await CreateRegisteredWidgetFromConfigAsync(
            config,
            keepPreparedForAnimation: true,
            showRaisedWhileInitializing: showRaisedWhileInitializing);
        if (window is not null)
        {
            await WaitForInitialContentReadyAsync(window);
        }

        return window;
    }

    private static async Task WaitForInitialContentReadyAsync(IDesktopWidgetWindow window)
    {
        if (window is not ContentWidgetWindow contentWindow)
        {
            return;
        }

        using var perfScope = PerformanceLogger.Measure(
            "WidgetManager.WaitForInitialContentReady");
        try
        {
            // ContentReadyTask covers the first LoadFolderContents. Showing
            // the window before it completes left the first reveal blank for
            // about the folder-load duration.
            await contentWindow.ContentReadyTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            App.Log(
                "[TrayBatch] Initial content readiness timed out; " +
                "showing the prepared surface.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log(
                $"[TrayBatch] Initial content readiness failed; " +
                $"showing the prepared surface: {ex.Message}");
        }
    }

    private void CancelActiveTrayAnimationsAndRestorePositions()
    {
        _trayBatchAnimationDriver.Cancel();
        TrayAnimationInterruptionCoordinator.CancelAndRestore(
            GetLoadedDesktopWindows(),
            window => window.CancelTrayAnimationAndRestorePosition(),
            (window, ex) => App.Log(
                $"[TrayBatch] Failed to reset interrupted animation " +
                $"{FormatHostWindow(window)}: {ex}"));
    }

    private void PlayPreparedTrayShowAnimations(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        App.LogVerbose($"[TrayBatch] Starting batch show for {windows.Count} widgets...");
        
        // ⭐ 统一驱动：同一时钟 + DeferWindowPos 原子批量提交，所有窗口锁步滑动
        try
        {
            // Step 1: 在同一帧内完成所有偏移量设置
                ApplyTrayAnimationGroupOffset(windows);
                long traceAnchor = Stopwatch.GetTimestamp();

                // Step 2: 收集所有窗口的共享动画条目（窗口自身的 Opacity/Scale
                // 仍由各自的 Composition 动画驱动）
                var entries = new List<WidgetTrayBatchAnimationEntry>(windows.Count);
                foreach (var window in windows)
                {
                    try
                    {
                        var entry = window.BeginSharedTrayShowAnimation();
                        if (entry is not null)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[WidgetManager] Failed to play widget show animation {window}: {ex}");
                    }
                }

                // Step 3: 单批启动；等待 1 帧让新显示的窗口先提交首帧表面
                var options = WidgetAnimationSettings.From(_settingsService.Settings);
                _trayBatchAnimationDriver.Start(
                    entries,
                    options.DurationMs,
                    _settingsService.Settings.WidgetAnimationEasingIntensity,
                    isShowing: true,
                    startDelayFrames: 1,
                    staggerIntervalMs: GetTrayStaggerIntervalMs(windows),
                    traceAnchorTimestamp: traceAnchor);
                _ = RestoreInteractionBackdropsWhenIdleAsync(windows);
        }
        catch (Exception ex)
        {
            App.Log($"[TrayBatch] Error during batch animation: {ex}");
        }
    }

    private void PrepareTrayShowAnimations(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        ApplyTrayAnimationGroupOffset(windows);
        foreach (var window in windows)
        {
            try
            {
                window.PrepareTrayShowAnimation();
                window.SimplifyBackdropForInteraction();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to prepare widget show animation {FormatHostWindow(window)}: {ex}");
            }
        }
    }

    private void PlayPreparedTrayHideAnimations(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        App.LogVerbose($"[TrayBatch] Starting batch hide for {windows.Count} widgets...");
        
        // ⭐ 与批量显示相同：统一驱动 + DeferWindowPos 原子批量提交
        try
        {
            // Step 1: 在同一帧内完成所有偏移量设置
                ApplyTrayAnimationGroupOffset(windows);
                long traceAnchor = Stopwatch.GetTimestamp();

                // Step 2: 收集所有窗口的共享隐藏动画条目
                var entries = new List<WidgetTrayBatchAnimationEntry>(windows.Count);
                foreach (var window in windows)
                {
                    try
                    {
                        var entry = window.BeginSharedTrayHideAnimation();
                        if (entry is not null)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[WidgetManager] Failed to play widget hide animation {FormatHostWindow(window)}: {ex}");
                    }
                }

                // Step 3: 单批启动，内容已渲染无需等待
                var options = WidgetAnimationSettings.From(_settingsService.Settings);
                _trayBatchAnimationDriver.Start(
                    entries,
                    options.DurationMs,
                    _settingsService.Settings.WidgetAnimationEasingIntensity,
                    isShowing: false,
                    startDelayFrames: 0,
                    staggerIntervalMs: GetTrayStaggerIntervalMs(windows),
                    traceAnchorTimestamp: traceAnchor);
                _ = RestoreInteractionBackdropsWhenIdleAsync(windows);
        }
        catch (Exception ex)
        {
            App.Log($"[TrayBatch] Error during batch animation: {ex}");
        }
    }

    /// <summary>
    /// Per-window stagger delay for batch animations. Zero keeps the default
    /// lockstep; when enabled the interval shrinks as the group grows so the
    /// whole batch stays within roughly one extra quarter-second.
    /// </summary>
    private int GetTrayStaggerIntervalMs(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (!_settingsService.Settings.WidgetAnimationStaggerEnabled || windows.Count < 2)
        {
            return 0;
        }

        return Math.Clamp(240 / (windows.Count - 1), 8, 48);
    }

    /// <summary>
    /// Restores each window's full blurred legacy acrylic once the batch slide
    /// finishes, so Win10 interaction simplification never outlives the tray
    /// animation. No-op for windows that were never simplified.
    /// </summary>
    private async Task RestoreInteractionBackdropsWhenIdleAsync(
        IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        try
        {
            await _trayBatchAnimationDriver.WaitForIdleAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[TrayBatch] Backdrop restore idle wait failed: {ex}");
        }

        foreach (var window in windows)
        {
            try
            {
                window.RestoreBackdropAfterInteraction();
            }
            catch (Exception ex)
            {
                App.Log($"[TrayBatch] Failed to restore backdrop {FormatHostWindow(window)}: {ex}");
            }
        }
    }

    private void ApplyTrayAnimationGroupOffset(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        foreach (var window in windows)
        {
            window.SetTrayAnimationOffsetOverride(null, null);
        }

        var options = WidgetAnimationSettings.From(_settingsService.Settings);
        if (!options.UsesGroupOffset)
        {
            return;
        }

        string direction = WidgetAnimationSettings.GetEffectiveSlideDirection(
            options.Effect, options.SlideDirection);
        if (direction == SettingsService.WidgetAnimationSlideDirectionNone)
        {
            return;
        }

        foreach (var group in windows.GroupBy(GetAnimationWorkAreaKey))
        {
            var groupWindows = group.ToList();
            if (groupWindows.Count == 0)
            {
                continue;
            }

            var workArea = GetAnimationWorkArea(groupWindows[0]);

            // Use resting bounds: during prepare/play the HWNDs are physically
            // displaced offscreen, which would collapse the group offset to ~0
            // and leave windows parked at their final position when uncloaked.
            double groupLeft = groupWindows.Min(window => window.RestingAnimationBounds.Left);
            double groupTop = groupWindows.Min(window => window.RestingAnimationBounds.Top);
            double groupRight = groupWindows.Max(window => window.RestingAnimationBounds.Right);
            double groupBottom = groupWindows.Max(window => window.RestingAnimationBounds.Bottom);

            // The group ejects past this monitor's work-area boundary on a
            // single axis; the other override stays 0. Windows crossing onto
            // an adjacent display dissolve per-window: each window fades over
            // its own boundary span inside the tray animation controller.
            double offsetX = 0;
            double offsetY = 0;
            switch (direction)
            {
                case SettingsService.WidgetAnimationSlideDirectionLeft:
                    offsetX = -(groupRight - workArea.X + OffscreenAnimationPadding);
                    break;

                case SettingsService.WidgetAnimationSlideDirectionUp:
                    offsetY = -(groupBottom - workArea.Y + OffscreenAnimationPadding);
                    break;

                case SettingsService.WidgetAnimationSlideDirectionDown:
                    offsetY = workArea.Y + workArea.Height - groupTop + OffscreenAnimationPadding;
                    break;

                default:
                    offsetX = workArea.X + workArea.Width - groupLeft + OffscreenAnimationPadding;
                    break;
            }

            double fullTravel = Math.Max(Math.Abs(offsetX), Math.Abs(offsetY));
            bool forceFade = false;
            if (fullTravel > 0)
            {
                double safeTravel = double.MaxValue;
                foreach (var window in groupWindows)
                {
                    safeTravel = Math.Min(
                        safeTravel,
                        window.ComputeTraySlideSafeTravel(offsetX, offsetY));
                }

                if (double.IsFinite(safeTravel) &&
                    safeTravel < fullTravel - 0.5)
                {
                    double scale = Math.Max(0, safeTravel) / fullTravel;
                    offsetX *= scale;
                    offsetY *= scale;
                    forceFade = true;
                    App.LogVerbose(
                        $"[TrayBatch] Capped slide to {safeTravel:F0}px " +
                        $"(full {fullTravel:F0}px) to keep the batch on one DPI");
                }
            }

            foreach (var window in groupWindows)
            {
                window.SetTrayAnimationOffsetOverride(offsetX, offsetY, forceFade);
            }
        }
    }

    private static string GetAnimationWorkAreaKey(IDesktopWidgetWindow window)
    {
        var workArea = GetAnimationWorkArea(window);
        return $"{workArea.X}:{workArea.Y}:{workArea.Width}:{workArea.Height}";
    }

    private static Windows.Graphics.RectInt32 GetAnimationWorkArea(IDesktopWidgetWindow window)
    {
        return GetAnimationDisplayArea(window).WorkArea;
    }

    private static DisplayArea GetAnimationDisplayArea(IDesktopWidgetWindow window)
    {
        var point = new Windows.Graphics.PointInt32(
            (int)Math.Round(window.RestingAnimationBounds.Left),
            (int)Math.Round(window.RestingAnimationBounds.Top));
        return DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary);
    }

    private static void ActivateIdleHighestWindow(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (GetWindowsInIdleHighestFirstOrder(windows).FirstOrDefault() is not { } window)
        {
            return;
        }

        try
        {
            window.ActivateRaisedFromTrayBatch();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetManager] Failed to activate raised widget {FormatHostWindow(window)}: {ex}");
        }
    }

    private void QueueTrayRaiseTopMostConfirmation(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        long generation = ++_trayRaiseBatchGeneration;
        ConfirmTrayRaiseTopMost(windows, generation);
    }

    private void ConfirmTrayRaiseTopMost(IReadOnlyList<IDesktopWidgetWindow> windows, long generation)
    {
        if (generation != _trayRaiseBatchGeneration || !_widgetsRaisedFromTray)
        {
            return;
        }

        IReadOnlyList<IDesktopWidgetWindow> visibleWindows =
            GetWindowsInIdleHighestFirstOrder(
                windows.Where(window => window.Visible));
        IReadOnlyList<IntPtr> visibleHandles = visibleWindows
            .Select(window => window.WindowHandle)
            .ToList();
        if (WidgetLayerService.UsesQuickRevealMode())
        {
            // Quick reveal behaves like a system flyout: it stays above the
            // newly activated application without taking focus, then releases
            // TOPMOST only from each window's hide-animation completion path.
            // Use every visible surface, not only the candidate windows: a
            // group host is one HWND shared by several member configs, and a
            // descendant that is not in this batch must still join the band.
            IReadOnlyList<IntPtr> quickRevealHandles = GetWindowsInIdleHighestFirstOrder(
                    GetLoadedDesktopWindows().Where(window => window.Visible))
                .Select(window => window.WindowHandle)
                .Distinct()
                .ToList();
            WidgetLayerService.HoldGroupTopMostWithoutActivation(quickRevealHandles);
            // Auxiliary surfaces that were already open before this raise
            // (settings, desktop organization) join the band
            // above the widgets; after the group hold, SetWindowTopMost leaves
            // each of them above the whole widget group.
            HoldVisibleAuxiliaryWindowsAboveRaisedWidgets();
            return;
        }

        IntPtr activeHandle = visibleWindows.FirstOrDefault()?.WindowHandle ?? IntPtr.Zero;
        WidgetLayerService.BringGroupTemporarilyToFront(
            visibleHandles,
            activeHandle);
        WidgetLayerService.ApplyPeerOrderHighestToLowest(visibleHandles);
    }

    private const int TrayRaiseBandRecheckDelayMs = 80;

    private const int QuickRevealTopMostRecheckDelayMs = 120;

    /// <summary>
    /// Quick-reveal re-asserts TOPMOST after the show animation settles.
    /// Desktop double-click intentionally keeps Explorer as the foreground
    /// owner, so the synchronous band hold can race the uncloak and leave a
    /// visible surface below a foreign window. Two bounded attempts cover the
    /// Explorer activation turn without fighting a later user switch: the
    /// generation and raised-state guards drop the retry as soon as the
    /// session is dismissed.
    /// </summary>
    private void QueueQuickRevealTopMostRecheck(
        IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        long generation = _trayRaiseBatchGeneration;
        UiDispatch.RunOrDefer(async () =>
        {
            foreach (int delay in new[]
                     {
                         QuickRevealTopMostRecheckDelayMs,
                         QuickRevealTopMostRecheckDelayMs + 200
                     })
            {
                await Task.Delay(delay);
                if (generation != _trayRaiseBatchGeneration ||
                    !_widgetsRaisedFromTray ||
                    !WidgetLayerService.UsesQuickRevealMode())
                {
                    return;
                }

                List<IntPtr> handles = GetWindowsInIdleHighestFirstOrder(
                        GetLoadedDesktopWindows().Where(window => window.Visible))
                    .Where(window => Win32Helper.IsWindow(window.WindowHandle))
                    .Select(window => window.WindowHandle)
                    .Distinct()
                    .ToList();
                if (handles.Count == 0)
                {
                    return;
                }

                int missingBefore = handles.Count(
                    handle => !Win32Helper.IsWindowTopMost(handle));
                WidgetLayerService.HoldGroupTopMostWithoutActivation(handles);
                if (missingBefore > 0)
                {
                    App.Log(
                        "[QuickReveal] Topmost re-asserted " +
                        $"generation={generation} count={handles.Count} " +
                        $"missingBefore={missingBefore}");
                }
                else
                {
                    App.LogVerbose(
                        "[QuickReveal] Topmost verified " +
                        $"generation={generation} count={handles.Count}");
                }
            }
        });
    }

    /// <summary>
    /// One-shot band-integrity recheck after the synchronous raise
    /// confirmation. While DeskBox is still a background process the group
    /// lift cannot cross the then-foreground window, so a raise can settle
    /// into a split band where only the activated widget crossed the line;
    /// the Activated-driven reassert repairs that only after the user clicks
    /// a widget, leaving the split visible until then. Unlike the repeated
    /// v1.0.7 confirmations (removed in v1.2.9 for fighting the user's
    /// foreground switches) this aborts unless DeskBox still owns the
    /// foreground, never re-lifts when a foreign window legitimately covers
    /// the whole band, and skips windows mid title-bar drag.
    /// </summary>
    private void QueueTrayRaiseBandRecheck(
        IReadOnlyList<IDesktopWidgetWindow> windows,
        long generation)
    {
        if (windows.Count == 0)
        {
            return;
        }

        UiDispatch.RunOrDefer(async () =>
        {
            await Task.Delay(TrayRaiseBandRecheckDelayMs);
            if (generation != _trayRaiseBatchGeneration ||
                !_widgetsRaisedFromTray ||
                _isTogglingWidgetsDesktopLayer ||
                IsWidgetInteractionActive ||
                !IsForegroundDeskBoxWindow())
            {
                return;
            }

            if (windows.Any(window => window.IsTitleBarDragTransitionActive))
            {
                return;
            }

            List<IntPtr> handles = windows
                .Where(window => window.Visible && Win32Helper.IsWindow(window.WindowHandle))
                .Select(window => window.WindowHandle)
                .ToList();
            if (handles.Count < 2 ||
                !WidgetLayerService.IsForeignWindowAboveRaisedPeers(
                    handles,
                    exemptShellDesktopWindows: true))
            {
                return;
            }

            WidgetLayerService.BringGroupTemporarilyToFront(handles, handles[0]);
            WidgetLayerService.ApplyPeerOrderHighestToLowest(handles);
            App.Log(
                "[ZOrder] Raised band split repaired by delayed recheck " +
                $"generation={generation} count={handles.Count} " +
                $"active=0x{handles[0].ToInt64():X}");
        });
    }

    private void SaveBatchVisibilityState()
    {
        _settingsService.SaveDebounced(notifySubscribers: false);
    }

}
