using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// #81 (part 2): Avalonia.Headless.XUnit's <c>AvaloniaTestCase.Run</c> synchronously
    /// blocks a worker thread per parallel test (<c>TaskAwaiter&lt;RunSummary&gt;.GetResult()</c>),
    /// while the single shared <c>HeadlessUnitTestSession</c> dispatcher worker — which
    /// actually runs those tests — is itself a <c>Task.Run</c> that needs a threadpool
    /// thread. On a 2-vCPU CI runner the default min worker count (= core count) lets the
    /// parallel blockers consume every pool thread, so the dispatcher worker can never be
    /// scheduled, the dispatched test Tasks never complete, and the testhost hangs in
    /// teardown until --blame-hang aborts it.
    ///
    /// Part 1 of the fix (RustPtySession loops moved off the threadpool onto dedicated
    /// background threads) removed an unbounded second consumer that previously masked
    /// AND amplified this (and is why an earlier SetMinThreads attempt didn't stick).
    /// This gives the pool guaranteed headroom so the dispatcher worker is always
    /// schedulable alongside the bounded set of parallel AvaloniaTestCase.Run blockers.
    /// </summary>
    internal static class TestHostThreadPoolConfig
    {
        [ModuleInitializer]
        internal static void Init()
        {
            ThreadPool.GetMinThreads(out _, out int completionPortThreads);
            int target = Math.Max(Environment.ProcessorCount * 4, 32);
            ThreadPool.SetMinThreads(target, completionPortThreads);
        }
    }
}
