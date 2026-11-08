namespace DeskBox.Tests;

public sealed class ExternalStateRecoveryContractTests
{
    [Fact]
    public void JumpListToggle_SkipsFileWidgetRefresh()
    {
        string app = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.xaml.cs"));

        Assert.Contains(
            "bool scheduledStateRecovery = ScheduleExternalStateRecovery();",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "JumpListService.IsToggleWidgetsActivation(jumpListArgument)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "_externalStateRecoveryRefreshFileWidgets",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "private bool ScheduleExternalStateRecovery()",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "RecoverExternalStateAsync(bool refreshFileWidgets)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetManager is not null && refreshFileWidgets",
            app,
            StringComparison.Ordinal);

        string jumpList = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/JumpListService.cs"));
        Assert.Contains(
            "public static bool IsToggleWidgetsActivation(string? argument)",
            jumpList,
            StringComparison.Ordinal);
    }
}
