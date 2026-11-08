using DeskBox.Models;

namespace DeskBox.Services;

internal readonly record struct StartupDisplaySettlingUsage(
    IReadOnlySet<string> UsedTokens,
    bool HasUnknownBinding);

/// <summary>
/// Decides whether startup should wait for a missing display before restoring
/// widgets. The previous predicate waited whenever the current display set was
/// a true subset of the active profile; a missing display that hosts no widget
/// does not justify that delay.
/// </summary>
internal static class StartupDisplaySettlingPolicy
{
    public static StartupDisplaySettlingUsage CollectUsedTokens(AppSettings settings)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasUnknownBinding = false;

        WidgetLayoutSettingsSlice layout = settings.WidgetLayout;
        WidgetTopologyLayoutProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(layout.ActiveWidgetTopologyKey) &&
            layout.WidgetTopologyLayouts.TryGetValue(
                layout.ActiveWidgetTopologyKey,
                out WidgetTopologyLayoutProfile? foundProfile))
        {
            profile = foundProfile;
        }

        IReadOnlyList<WidgetTopologyMonitorProfile> monitors =
            profile?.Monitors ?? [];

        void AddBinding(
            string? stableId,
            string? deviceName,
            WidgetScreenBindingMode bindingMode,
            string? boundScreenId)
        {
            if (bindingMode == WidgetScreenBindingMode.Pinned)
            {
                if (!TryAddToken(boundScreenId, deviceName: null, monitors, used))
                {
                    hasUnknownBinding = true;
                }

                return;
            }

            if (bindingMode == WidgetScreenBindingMode.FollowPrimary)
            {
                WidgetTopologyMonitorProfile? primary =
                    monitors.FirstOrDefault(monitor => monitor.IsPrimary);
                if (primary is not null)
                {
                    used.Add(DisplayIdentityTokens.TokenFor(primary));
                }
                else
                {
                    hasUnknownBinding = true;
                }

                return;
            }

            if (!TryAddToken(stableId, deviceName, monitors, used))
            {
                hasUnknownBinding = true;
            }
        }

        foreach (WidgetConfig widget in layout.Widgets)
        {
            if (widget.IsDisabled)
            {
                continue;
            }

            AddBinding(
                widget.PositionMonitorStableId,
                widget.PositionMonitorDeviceName,
                widget.ScreenBindingMode,
                widget.BoundScreenId);
        }

        foreach (WidgetGroupConfig group in layout.WidgetGroups)
        {
            if (!group.IsVisible)
            {
                continue;
            }

            AddBinding(
                group.PositionMonitorStableId,
                group.PositionMonitorDeviceName,
                group.ScreenBindingMode,
                group.BoundScreenId);
        }

        if (profile is not null)
        {
            foreach (WidgetSurfaceLayoutProfile surface in profile.Surfaces.Values)
            {
                if (!TryAddToken(
                        surface.PositionMonitorStableId,
                        deviceName: null,
                        monitors,
                        used))
                {
                    hasUnknownBinding = true;
                }
            }
        }

        return new(used, hasUnknownBinding);
    }

    public static bool ShouldWait(
        IReadOnlyCollection<string> activeTokens,
        IReadOnlyCollection<string> currentTokens,
        StartupDisplaySettlingUsage usage)
    {
        if (activeTokens.Count == 0 ||
            !DisplayIdentityTokens.IsTrueSubset(currentTokens, activeTokens))
        {
            return false;
        }

        if (usage.HasUnknownBinding)
        {
            return true;
        }

        if (usage.UsedTokens.Count == 0)
        {
            return false;
        }

        var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in currentTokens)
        {
            remaining[token] = remaining.GetValueOrDefault(token) + 1;
        }

        foreach (string token in activeTokens)
        {
            if (remaining.TryGetValue(token, out int count) && count > 0)
            {
                remaining[token] = count - 1;
                continue;
            }

            if (usage.UsedTokens.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAddToken(
        string? stableId,
        string? deviceName,
        IReadOnlyList<WidgetTopologyMonitorProfile> monitors,
        HashSet<string> used)
    {
        if (!string.IsNullOrWhiteSpace(stableId) &&
            !DisplayPlacementResolver.IsDegenerateIdentity(stableId))
        {
            used.Add(DisplayIdentityTokens.TokenFor(stableId, width: 0, height: 0));
            return true;
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            WidgetTopologyMonitorProfile? match = monitors.FirstOrDefault(monitor =>
                string.Equals(
                    monitor.DeviceName,
                    deviceName,
                    StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                used.Add(DisplayIdentityTokens.TokenFor(match));
                return true;
            }
        }

        return false;
    }
}
