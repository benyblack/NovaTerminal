using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NovaTerminal.Tests.Infra
{
    public static class DeadlockDetection
    {
        public static async Task RunWithTimeout(Func<Task> action, int timeoutMs, string context)
        {
            var cts = new CancellationTokenSource();
            var task = action();
            var completedTask = await Task.WhenAny(task, Task.Delay(timeoutMs, cts.Token));

            if (completedTask == task)
            {
                cts.Cancel();
                await task; // Propagation of exceptions
            }
            else
            {
                throw new TimeoutException($"Potential Deadlock detected in {context}! Operation failed to complete within {timeoutMs}ms.");
            }
        }
    }
}
