using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class StartupDisplaySettlingPolicyTests
{
    [Fact]
    public void CollectUsedTokens_ReadsWidgetGroupSurfaceAndPinnedBindings()
    {
        AppSettings settings = CreateSettings();
        settings.ActiveWidgetTopologyKey = "topology";
        var profile = new WidgetTopologyLayoutProfile();
        profile.Monitors.Add(new WidgetTopologyMonitorProfile
        {
            StableId = "pnp:display-1",
            DeviceName = @"\\.\DISPLAY1",
            IsPrimary = true
        });
        profile.Surfaces["surface"] = new WidgetSurfaceLayoutProfile
        {
            PositionMonitorStableId = "pnp:display-2"
        };
        settings.WidgetTopologyLayouts["topology"] = profile;
        settings.Widgets.Add(new WidgetConfig
        {
            PositionMonitorStableId = "pnp:display-3",
            ScreenBindingMode = WidgetScreenBindingMode.Unbound
        });
        settings.WidgetGroups.Add(new WidgetGroupConfig
        {
            ScreenBindingMode = WidgetScreenBindingMode.Pinned,
            BoundScreenId = "pnp:display-4"
        });

        StartupDisplaySettlingUsage usage =
            StartupDisplaySettlingPolicy.CollectUsedTokens(settings);

        Assert.Contains("PNP:DISPLAY-2", usage.UsedTokens);
        Assert.Contains("PNP:DISPLAY-3", usage.UsedTokens);
        Assert.Contains("PNP:DISPLAY-4", usage.UsedTokens);
        Assert.False(usage.HasUnknownBinding);
    }

    [Fact]
    public void CollectUsedTokens_FollowPrimaryMapsToPrimaryMonitor()
    {
        AppSettings settings = CreateSettings();
        settings.ActiveWidgetTopologyKey = "topology";
        var profile = new WidgetTopologyLayoutProfile();
        profile.Monitors.Add(new WidgetTopologyMonitorProfile
        {
            StableId = "pnp:primary",
            IsPrimary = true
        });
        profile.Monitors.Add(new WidgetTopologyMonitorProfile
        {
            StableId = "pnp:secondary"
        });
        settings.WidgetTopologyLayouts["topology"] = profile;
        settings.Widgets.Add(new WidgetConfig
        {
            ScreenBindingMode = WidgetScreenBindingMode.FollowPrimary
        });

        StartupDisplaySettlingUsage usage =
            StartupDisplaySettlingPolicy.CollectUsedTokens(settings);

        Assert.Contains("PNP:PRIMARY", usage.UsedTokens);
        Assert.True(StartupDisplaySettlingPolicy.ShouldWait(
            new[] { "PNP:PRIMARY", "PNP:SECONDARY" },
            new[] { "PNP:SECONDARY" },
            usage));
    }

    [Fact]
    public void CollectUsedTokens_DegenerateIdentityMapsByDeviceName()
    {
        AppSettings settings = CreateSettings();
        settings.ActiveWidgetTopologyKey = "topology";
        var profile = new WidgetTopologyLayoutProfile();
        profile.Monitors.Add(new WidgetTopologyMonitorProfile
        {
            StableId = @"\\.\DISPLAY1",
            DeviceName = @"\\.\DISPLAY1",
            MonitorWidth = 1920,
            MonitorHeight = 1080
        });
        settings.WidgetTopologyLayouts["topology"] = profile;
        settings.Widgets.Add(new WidgetConfig
        {
            PositionMonitorStableId = @"\\.\DISPLAY1",
            PositionMonitorDeviceName = @"\\.\DISPLAY1"
        });

        StartupDisplaySettlingUsage usage =
            StartupDisplaySettlingPolicy.CollectUsedTokens(settings);

        Assert.Contains("geo:1920x1080", usage.UsedTokens);
        Assert.False(usage.HasUnknownBinding);
    }

    [Fact]
    public void ShouldWait_MissingDisplayWithoutBinding_ReturnsFalse()
    {
        var usage = new StartupDisplaySettlingUsage(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PNP:DISPLAY-1" },
            HasUnknownBinding: false);

        Assert.False(StartupDisplaySettlingPolicy.ShouldWait(
            new[] { "PNP:DISPLAY-1", "PNP:DISPLAY-2" },
            new[] { "PNP:DISPLAY-1" },
            usage));
    }

    [Fact]
    public void ShouldWait_MissingDisplayWithBinding_ReturnsTrue()
    {
        var usage = new StartupDisplaySettlingUsage(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PNP:DISPLAY-2" },
            HasUnknownBinding: false);

        Assert.True(StartupDisplaySettlingPolicy.ShouldWait(
            new[] { "PNP:DISPLAY-1", "PNP:DISPLAY-2" },
            new[] { "PNP:DISPLAY-1" },
            usage));
    }

    [Fact]
    public void ShouldWait_NonSubset_ReturnsFalse()
    {
        var usage = new StartupDisplaySettlingUsage(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PNP:DISPLAY-2" },
            HasUnknownBinding: false);

        Assert.False(StartupDisplaySettlingPolicy.ShouldWait(
            new[] { "PNP:DISPLAY-1", "PNP:DISPLAY-2" },
            new[] { "PNP:DISPLAY-1", "PNP:DISPLAY-3" },
            usage));
    }

    [Fact]
    public void ShouldWait_DuplicateGeoToken_UsesMultisetDifference()
    {
        var usage = new StartupDisplaySettlingUsage(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "geo:1920x1080" },
            HasUnknownBinding: false);

        Assert.True(StartupDisplaySettlingPolicy.ShouldWait(
            new[] { "geo:1920x1080", "geo:1920x1080" },
            new[] { "geo:1920x1080" },
            usage));
    }

    [Fact]
    public void ShouldWait_UnknownLegacyBinding_IsConservative()
    {
        var usage = new StartupDisplaySettlingUsage(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            HasUnknownBinding: true);

        Assert.True(StartupDisplaySettlingPolicy.ShouldWait(
            new[] { "PNP:DISPLAY-1", "PNP:DISPLAY-2" },
            new[] { "PNP:DISPLAY-1" },
            usage));
    }

    private static AppSettings CreateSettings()
    {
        var settings = new AppSettings();
        settings.Widgets = [];
        settings.WidgetGroups = [];
        settings.WidgetTopologyLayouts = [];
        return settings;
    }
}
