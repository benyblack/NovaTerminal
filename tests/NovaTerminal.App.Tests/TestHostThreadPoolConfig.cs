using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Threadpool headroom for a test host that blocks a worker thread per parallel test.
    /// Avalonia.Headless.XUnit's <c>AvaloniaTestCase.Run</c> ends in
    /// <c>TaskAwaiter&lt;RunSummary&gt;.GetResult()</c>, and the single shared
    /// <c>HeadlessUnitTestSession</c> dispatcher worker that actually runs those tests is
    /// itself a <c>Task.Run</c> needing a pool thread. On a 2-vCPU runner the default min
    /// worker count is the core count, which leaves the two competing for the same few
    /// threads.
    /// </summary>
    /// <remarks>
    /// <strong>This was written as the fix for #81 and is not one.</strong> The claim it
    /// carried — that the parallel blockers starved the dispatcher worker out of the pool
    /// and that starvation was the testhost hang — did not survive the dumps. All four
    /// specimens showed idle workers, one of them seventeen, so the dispatcher worker was
    /// schedulable in every hang that was ever captured. The actual cause is a throw out of
    /// <c>EnsureIsolatedApplication()</c>, which sits outside the try in
    /// <c>HeadlessUnitTestSession.DispatchCore</c> and so unwinds the assembly's one
    /// dispatcher loop; <c>TerminalPane.InitializeCommandAssist</c> carries the full account
    /// and PR #416 the fix.
    /// <para>
    /// Kept because giving a pool headroom is defensible for this host on its own terms —
    /// the blocking described above is real whatever it did or did not cause — and because
    /// removing it would be a change to the lane's scheduling made on no evidence, which is
    /// how it came to be here in the first place. What is removed is the causal claim.
    /// </para>
    /// </remarks>
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
