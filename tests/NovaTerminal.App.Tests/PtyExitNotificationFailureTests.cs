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
    /// Notifying the exit must not be able to kill the thread doing it.
    ///
    /// <c>OnExit</c> raises arbitrary subscriber code, exactly like <c>OnOutputReceived</c> — and
    /// this class deliberately does not guard either invocation, because the loops' own catch-alls
    /// are where subscriber exceptions are contained (see the comment on ProcessLoop). The exit
    /// notification was the one call that sat *outside* that protection at both of the sites that
    /// run on a dedicated thread:
    ///
    ///   * ProcessLoop calls it after its try/catch, so a throwing subscriber escaped the thread
    ///     entirely — an unhandled exception on a dedicated thread, which tears down the process
    ///     rather than the session.
    ///   * ReadLoop calls it from its `finally` on the read-failure path, *before* the teardown
    ///     that cancels the token and completes the output queue. A throw there escaped the whole
    ///     try statement and took the teardown with it, so the session was left half-alive with
    ///     ProcessLoop parked on a queue nobody would ever complete.
    ///
    /// Both are reachable from the public API by one badly behaved event handler.
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class PtyExitNotificationFailureTests
    {
        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task AThrowingExitSubscriberDoesNotEscapeTheProcessLoop()
        {
            // EOF, so ReadLoop's finally does not claim the exit and ProcessLoop is the first
            // (and only) caller of TryNotifyExit. This isolates ProcessLoop's call site.
            using var log = PtyLogCapture.Attach();

            int eof = 0;
            using var session = NewSession((handle, buffer, length) =>
            {
                if (Volatile.Read(ref eof) == 1)
                {
                    return 0;
                }

                Thread.Sleep(25);
                buffer[0] = (byte)'x';
                return 1;
            });

            Thread process = await WaitForLoopThreadAsync(() => session.ProcessLoopThread);

            // Subscribe before releasing the EOF: the loops start in the constructor, so a read
            // substitute that reports EOF immediately can notify the exit before the handler is
            // attached, and the test would assert nothing at all.
            session.OnExit += _ => throw new InvalidOperationException("exit subscriber blew up");
            Volatile.Write(ref eof, 1);

            await WaitUntilAsync(() => !process.IsAlive, TimeSpan.FromSeconds(20));

            string logText = log.ToString();
            Assert.Contains("Exit notification failed", logText, StringComparison.Ordinal);
            Assert.False(
                process.IsAlive,
                $"ProcessLoop must exit normally after a throwing subscriber.\nPTY log:\n{logText}");
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task AThrowingExitSubscriberDoesNotSkipTheReadFailureTeardown()
        {
            // Repeated read failures make ReadLoop give up and claim the exit code from its
            // `finally`, ahead of the teardown. TryNotifyExit is first-caller-wins, so this is
            // the site under test and ProcessLoop's later call is a no-op.
            using var log = PtyLogCapture.Attach();

            int failReads = 0;
            using var session = NewSession((handle, buffer, length) =>
            {
                if (Volatile.Read(ref failReads) == 1)
                {
                    return -1;
                }

                Thread.Sleep(25);
                buffer[0] = (byte)'x';
                return 1;
            });

            Thread process = await WaitForLoopThreadAsync(() => session.ProcessLoopThread);

            session.OnExit += _ => throw new InvalidOperationException("exit subscriber blew up");
            Volatile.Write(ref failReads, 1);

            await WaitUntilAsync(() => !process.IsAlive, TimeSpan.FromSeconds(30));

            string logText = log.ToString();
            Assert.Contains("Exit notification failed", logText, StringComparison.Ordinal);

            // The real damage a throw did here was not the lost notification but the skipped
            // teardown: without _cts.Cancel() and CompleteAdding(), ProcessLoop stays blocked on
            // the output queue for the life of the process. A dead ProcessLoop thread is the
            // observable proof that the teardown ran anyway.
            Assert.False(
                process.IsAlive,
                $"the read-failure teardown must still run after a throwing subscriber.\nPTY log:\n{logText}");
            Assert.Equal(RustPtySession.ReadFailureExitCode, session.ExitCode);
        }

        private static RustPtySession NewSession(RustPtySession.PtyReadDelegate readFromPty)
        {
            return new RustPtySession(
                ShellHelper.GetDefaultShell(),
                80,
                24,
                args: null,
                cwd: null,
                // Irrelevant here, and its SendInput would race the substituted read loop.
                skipPowerShellPostLaunchInit: true,
                environmentOverrides: null,
                readFromPty: readFromPty);
        }

        private static async Task<Thread> WaitForLoopThreadAsync(Func<Thread?> accessor)
        {
            await WaitUntilAsync(() => accessor() is not null, TimeSpan.FromSeconds(10));
            return accessor() ?? throw new TimeoutException("the PTY loop thread did not start");
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.Elapsed < timeout)
            {
                await Task.Delay(25);
            }
        }
    }
}
