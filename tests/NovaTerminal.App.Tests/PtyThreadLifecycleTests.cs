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

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task Dispose_WhileReadBlocked_JoinsLoopThreadsPromptly()
        {
            string shell = ShellHelper.GetDefaultShell();
            var session = new RustPtySession(shell, 80, 24);

            await WaitUntilAsync(
                () => session.ReadLoopThread is not null && session.ProcessLoopThread is not null,
                TimeSpan.FromSeconds(5));

            Thread read = session.ReadLoopThread!;
            Thread process = session.ProcessLoopThread!;

            // Let the shell go idle (read parked in native pty_read).
            await Task.Delay(500);

            var sw = Stopwatch.StartNew();
            session.Dispose();
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                $"Dispose took {sw.Elapsed.TotalSeconds:F1}s — cancel/join did not unblock the read");
            Assert.False(read.IsAlive, "ReadLoop thread should have exited after Dispose");
            Assert.False(process.IsAlive, "ProcessLoop thread should have exited after Dispose");
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public void Dispose_IsIdempotent()
        {
            string shell = ShellHelper.GetDefaultShell();
            var session = new RustPtySession(shell, 80, 24);

            var ex = Record.Exception(() =>
            {
                session.Dispose();
                session.Dispose();
                session.Dispose();
            });

            Assert.Null(ex);
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
