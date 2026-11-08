namespace DeskBox.Tests;

public sealed class TrayRevealContentReadyContractTests
{
    [Fact]
    public void BatchReveal_WaitsForInitialContentBeforeShowing()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.TrayAnimation.cs"));

        Assert.Contains(
            "private static async Task WaitForInitialContentReadyAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await contentWindow.ContentReadyTask.WaitAsync(TimeSpan.FromSeconds(5));",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await WaitForInitialContentReadyAsync(existingContent);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await WaitForInitialContentReadyAsync(createdContent);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await WaitForInitialContentReadyAsync(existing);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await WaitForInitialContentReadyAsync(window);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Initial content readiness timed out",
            source,
            StringComparison.Ordinal);
    }
}
