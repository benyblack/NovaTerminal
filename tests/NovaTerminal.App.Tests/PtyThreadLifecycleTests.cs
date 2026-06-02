using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Regression guard for #81: ~16 leaked RustPtySession read/process loops on
    /// threadpool threads saturated the 17-worker pool (0 idle) on 2-vCPU CI runners,
    /// starving the xUnit run-completion continuation and hanging the testhost teardown.
    /// The loops must run on dedicated background threads so a leaked session can never
    /// consume the threadpool.
    /// </summary>
    public class PtyThreadLifecycleTests
    {
        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task BackgroundLoops_RunOnDedicatedBackgroundThreads_NotThreadPool()
        {
            string shell = ShellHelper.GetDefaultShell();
            using var session = new RustPtySession(shell, 80, 24);

            await WaitUntilAsync(
                () => session.ReadLoopThread is not null && session.ProcessLoopThread is not null,
                TimeSpan.FromSeconds(5));

            Thread read = session.ReadLoopThread!;
            Thread process = session.ProcessLoopThread!;

            Assert.False(read.IsThreadPoolThread, "ReadLoop must not run on a threadpool thread");
            Assert.False(process.IsThreadPoolThread, "ProcessLoop must not run on a threadpool thread");

            Assert.True(read.IsBackground, "ReadLoop thread must be a background thread");
            Assert.True(process.IsBackground, "ProcessLoop thread must be a background thread");
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed > timeout)
                    throw new TimeoutException("PTY loop threads did not start within the timeout.");
                await Task.Delay(25);
            }
        }
    }
}
