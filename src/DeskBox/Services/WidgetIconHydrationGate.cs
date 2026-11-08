namespace DeskBox.Services;

internal static class WidgetIconHydrationGate
{
    private static readonly object s_lock = new();
    private static int s_pauseCount;
    private static TaskCompletionSource? s_resume;

    public static IDisposable Pause()
    {
        lock (s_lock)
        {
            s_pauseCount++;
        }

        return new Scope();
    }

    public static async Task WaitForResumeAsync(CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (s_lock)
        {
            if (s_pauseCount == 0)
            {
                return;
            }

            s_resume ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            waitTask = s_resume.Task;
        }

        await waitTask.WaitAsync(cancellationToken);
    }

    private static void Resume()
    {
        TaskCompletionSource? resume;
        lock (s_lock)
        {
            if (s_pauseCount == 0 || --s_pauseCount > 0)
            {
                return;
            }

            resume = s_resume;
            s_resume = null;
        }

        resume?.TrySetResult();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Resume();
            }
        }
    }
}
