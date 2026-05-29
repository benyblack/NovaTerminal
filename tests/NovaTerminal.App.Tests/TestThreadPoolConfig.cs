using System.Runtime.CompilerServices;

namespace NovaTerminal.Tests;

/// <summary>
/// Raises the worker-thread floor for the test host before any test runs.
/// </summary>
/// <remarks>
/// Dump-confirmed root cause of the CI flakes #79 (TaskCanceledException) and #81
/// (8-minute testhost teardown/inactivity hang): <c>Avalonia.Headless.XUnit</c>'s
/// <c>AvaloniaTestCase.Run</c> executes each <c>[AvaloniaFact]</c> by synchronously
/// blocking a thread-pool thread (<c>Task.GetResult()</c> -> <c>SpinThenBlockingWait</c>)
/// while the test body runs on the single shared headless dispatcher. Under xUnit's
/// parallel collection execution on a low-core CI runner, every available pool thread
/// ends up blocked this way, so the dispatcher's own async continuations cannot get a
/// worker thread -> classic sync-over-async thread-pool starvation. The run then either
/// deadlocks (host never exits, blame-hang aborts at 8 min -> #81) or a dispatcher
/// operation is cancelled under the contention (-> #79).
///
/// Raising the minimum worker-thread count guarantees the pool hands out spare threads
/// immediately instead of throttling, so the dispatcher always makes progress. This
/// fixes the root cause for both flakes without disabling test parallelism. It runs in
/// the test host only and does not affect production.
/// </remarks>
internal static class TestThreadPoolConfig
{
    [ModuleInitializer]
    internal static void RaiseWorkerThreadFloor()
    {
        int floor = Math.Max(Environment.ProcessorCount * 4, 32);
        ThreadPool.GetMinThreads(out int workerMin, out int completionPortMin);
        if (workerMin < floor)
        {
            ThreadPool.SetMinThreads(floor, completionPortMin);
        }
    }
}
