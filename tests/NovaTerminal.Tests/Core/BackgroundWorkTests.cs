using NovaTerminal.Services;

namespace NovaTerminal.Tests.Core;

public sealed class BackgroundWorkTests
{
    [Fact]
    public async Task RunBlockingAsync_ReturnsPendingTaskWhileBlockingWorkRuns()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> task = BackgroundWork.RunBlockingAsync(_ =>
        {
            started.SetResult();
            release.Task.GetAwaiter().GetResult();
            return 42;
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(task.IsCompleted);

        release.SetResult();

        int result = await task;

        Assert.Equal(42, result);
    }

    [Fact]
    public void RunBlockingAsync_ThrowsImmediatelyForCanceledToken()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Action act = () => BackgroundWork.RunBlockingAsync(_ => 1, cts.Token);

        Assert.Throws<OperationCanceledException>(act);
    }
}
