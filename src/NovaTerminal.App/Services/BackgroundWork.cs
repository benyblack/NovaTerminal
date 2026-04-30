using System;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Services;

internal static class BackgroundWork
{
    internal static Task<T> RunBlockingAsync<T>(
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return work(cancellationToken);
        }, cancellationToken);
    }
}
