using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetIconHydrationGateTests
{
    [Fact]
    public async Task Pause_HoldsWaitersUntilDispose()
    {
        var pause = WidgetIconHydrationGate.Pause();
        Task wait = WidgetIconHydrationGate.WaitForResumeAsync(
            CancellationToken.None);

        await Task.Delay(20);
        Assert.False(wait.IsCompleted);

        pause.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task NestedPauses_ResumeOnlyAfterTheLastDispose()
    {
        var first = WidgetIconHydrationGate.Pause();
        var second = WidgetIconHydrationGate.Pause();
        Task wait = WidgetIconHydrationGate.WaitForResumeAsync(
            CancellationToken.None);

        first.Dispose();
        await Task.Delay(20);
        Assert.False(wait.IsCompleted);

        second.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CanceledWaiter_ThrowsAndDoesNotLeakPause()
    {
        using var pause = WidgetIconHydrationGate.Pause();
        using var cancellation = new CancellationTokenSource();
        Task wait = WidgetIconHydrationGate.WaitForResumeAsync(
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public void RevealPaths_PauseHydrationForTheWholeShow()
    {
        string viewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs"));
        Assert.Contains(
            "WidgetIconHydrationGate.WaitForResumeAsync(cancellation.Token)",
            viewModel,
            StringComparison.Ordinal);

        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        Assert.Contains(
            "using var hydrationPause = WidgetIconHydrationGate.Pause();",
            manager,
            StringComparison.Ordinal);

        string trayAnimation = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.TrayAnimation.cs"));
        Assert.Contains(
            "using var hydrationPause = WidgetIconHydrationGate.Pause();",
            trayAnimation,
            StringComparison.Ordinal);
    }
}
