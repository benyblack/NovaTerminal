using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Covers the read-loop exit paths added for #107, which were unreachable when that
    /// fix shipped: <c>Native.pty_read</c> is a static P/Invoke, so nothing could force a
    /// failure. The read call is now injectable, while the session still spawns a real
    /// shell — so the teardown runs against a real handle and a real child process rather
    /// than a mock of one.
    /// </summary>
    public class PtyReadFailureExitTests
    {
        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task RepeatedReadFailures_GiveUpAndReportTheFailureExitCode()
        {
            int reads = 0;
            using var session = NewSession((handle, buffer, length) =>
            {
                Interlocked.Increment(ref reads);
                return -1;
            });

            int? code = await WaitForExitAsync(session, TimeSpan.FromSeconds(20));

            Assert.Equal(RustPtySession.ReadFailureExitCode, code);

            // Bounded, not endless: the loop must stop at the limit rather than keep
            // calling. A couple of extra reads are tolerated for the racing final pass.
            Assert.InRange(
                Volatile.Read(ref reads),
                RustPtySession.MaxConsecutiveReadErrors,
                RustPtySession.MaxConsecutiveReadErrors + 2);
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task RepeatedReadFailures_TearTheSessionDownRatherThanJustReporting()
        {
            // The defect caught in review of #214: reporting the exit while leaving the
            // child process, writer thread and native handle alive left the UI showing a
            // terminated session that was still running.
            using var session = NewSession((handle, buffer, length) => -1);

            int? code = await WaitForExitAsync(session, TimeSpan.FromSeconds(20));
            Assert.Equal(RustPtySession.ReadFailureExitCode, code);

            Assert.False(
                session.IsProcessRunning,
                "a session that gave up reading must not still report itself as running");
            Assert.Equal(RustPtySession.ReadFailureExitCode, session.ExitCode);
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task Eof_StillReportsACleanExit()
        {
            // The other side of the change: introducing a failure code must not reclassify
            // a normal end-of-stream. This is the assertion the removed timing-dependent
            // test was reaching for, now deterministic.
            using var session = NewSession((handle, buffer, length) => 0);

            int? code = await WaitForExitAsync(session, TimeSpan.FromSeconds(20));

            Assert.Equal(0, code);
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task ASuccessfulReadResetsTheFailureCounter()
        {
            // Transient failures must not accumulate across healthy reads, or a session
            // that hiccups once every MaxConsecutiveReadErrors reads would eventually be
            // torn down despite working fine.
            int calls = 0;
            using var session = NewSession((handle, buffer, length) =>
            {
                int call = Interlocked.Increment(ref calls);

                // Fail one short of the limit, succeed, then repeat - several times over.
                // If the counter did not reset, this would trip the limit and exit.
                if (call % RustPtySession.MaxConsecutiveReadErrors != 0)
                {
                    return -1;
                }

                byte[] payload = Encoding.UTF8.GetBytes("x");
                payload.CopyTo(buffer, 0);
                return payload.Length;
            });

            int? code = await WaitForExitAsync(session, TimeSpan.FromSeconds(6));

            Assert.Null(code);
            Assert.True(
                session.IsProcessRunning,
                "the session should still be running: no failure run ever reached the limit");
        }

        private static RustPtySession NewSession(RustPtySession.PtyReadDelegate readFromPty)
        {
            // A real shell is still spawned so the teardown acts on a real handle; only
            // the read call is substituted.
            return new RustPtySession(
                ShellHelper.GetDefaultShell(),
                80,
                24,
                args: null,
                cwd: null,
                // Keep the PowerShell init injection out of the way: it is irrelevant here
                // and its SendInput would race the substituted read loop.
                skipPowerShellPostLaunchInit: true,
                environmentOverrides: null,
                readFromPty: readFromPty);
        }

        /// Returns the reported exit code, or null if none arrived within the timeout.
        ///
        /// Falls back to <see cref="RustPtySession.ExitCode"/> rather than relying solely
        /// on the event: the read loop starts inside the constructor, so with a read
        /// substitute that returns immediately (EOF) the exit can be reported before this
        /// method has a chance to subscribe. TryNotifyExit assigns ExitCode before raising
        /// OnExit, so the recorded value is authoritative either way. Without this the test
        /// passes or fails on scheduling luck.
        private static async Task<int?> WaitForExitAsync(RustPtySession session, TimeSpan timeout)
        {
            int? observed = null;
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            session.OnExit += code =>
            {
                observed = code;
                exited.TrySetResult(true);
            };

            if (session.ExitCode.HasValue)
            {
                return session.ExitCode;
            }

            await Task.WhenAny(exited.Task, Task.Delay(timeout));
            return observed ?? session.ExitCode;
        }
    }
}
