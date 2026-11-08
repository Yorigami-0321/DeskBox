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
        Assert.Contains(
            "skipIfRecentlyLoaded: true",
            app,
            StringComparison.Ordinal);

        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        Assert.Contains(
            "bool skipIfRecentlyLoaded = false",
            manager,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsFolderLoadRecentOrActive",
            manager,
            StringComparison.Ordinal);

        string viewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/WidgetViewModel.cs"));
        Assert.Contains(
            "IsFolderLoadRecentOrActive",
            viewModel,
            StringComparison.Ordinal);

        string jumpList = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/JumpListService.cs"));
        Assert.Contains(
            "public static bool IsToggleWidgetsActivation(string? argument)",
            jumpList,
            StringComparison.Ordinal);
    }
}
