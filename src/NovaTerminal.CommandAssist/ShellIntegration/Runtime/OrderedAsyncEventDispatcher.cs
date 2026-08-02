using System;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.CommandAssist.ShellIntegration.Runtime;

public sealed class OrderedAsyncEventDispatcher
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task EnqueueAsync(Func<Task> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
