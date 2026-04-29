using NovaTerminal.Services;

namespace NovaTerminal.Tests.Core;

public sealed class BackgroundWorkTests
{
    [Fact]
    public async Task RunBlockingAsync_ReturnsPendingTaskWhileBlockingWorkRuns()
    {
        using ManualResetEventSlim started = new(false);
        using ManualResetEventSlim release = new(false);

        Task<int> task = BackgroundWork.RunBlockingAsync(_ =>
        {
            started.Set();
            release.Wait();
            return 42;
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(1)));
        Assert.False(task.IsCompleted);

        release.Set();

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
