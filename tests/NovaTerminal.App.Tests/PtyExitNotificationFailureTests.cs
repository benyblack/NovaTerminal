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

            // A real pty_read parks until the shell writes something or the stream ends, so the
            // substitute parks too - on a gate the test opens. That ordering is the whole test:
            // the loops start in the constructor, so a substitute that reports EOF straight away
            // can notify the exit before the handler is attached, leaving the test asserting
            // nothing. A gate makes "not until I say so" explicit instead of inferring it from a
            // sleep being longer than the test's own setup.
            using var readyForEof = new ManualResetEventSlim(false);
            using var session = NewSession((handle, buffer, length) =>
            {
                // Bounded so a failing test cannot park this thread indefinitely.
                readyForEof.Wait(TimeSpan.FromSeconds(10));
                return 0;
            });

            Thread process = await WaitForLoopThreadAsync(() => session.ProcessLoopThread);

            session.OnExit += _ => throw new InvalidOperationException("exit subscriber blew up");
            readyForEof.Set();

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

            // Parked until the test opens the gate, then failing for good - so the read loop
            // exhausts MaxConsecutiveReadErrors and reaches its `finally` only after the throwing
            // handler is attached. The loop's own bounded retry paces the failures; nothing here
            // needs to.
            using var readyToFail = new ManualResetEventSlim(false);
            using var session = NewSession((handle, buffer, length) =>
            {
                readyToFail.Wait(TimeSpan.FromSeconds(10));
                return -1;
            });

            Thread process = await WaitForLoopThreadAsync(() => session.ProcessLoopThread);

            session.OnExit += _ => throw new InvalidOperationException("exit subscriber blew up");
            readyToFail.Set();

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
