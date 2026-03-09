using NovaTerminal.CommandAssist.ShellIntegration.Runtime;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

public sealed class OrderedAsyncEventDispatcherTests
{
    [Fact]
    public async Task EnqueueAsync_RunsWorkInSubmissionOrder()
    {
        var dispatcher = new OrderedAsyncEventDispatcher();
        var order = new List<int>();
        var firstCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = dispatcher.EnqueueAsync(async () =>
        {
            order.Add(1);
            await firstCanComplete.Task;
            order.Add(2);
        });

        Task second = dispatcher.EnqueueAsync(() =>
        {
            order.Add(3);
            secondStarted.SetResult();
            return Task.CompletedTask;
        });

        await Task.Delay(50);
        Assert.Equal(new[] { 1 }, order);

        firstCanComplete.SetResult();
        await Task.WhenAll(first, second);
        await secondStarted.Task;

        Assert.Equal(new[] { 1, 2, 3 }, order);
    }
}
