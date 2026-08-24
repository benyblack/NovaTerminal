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
    /// The child-status probe must survive a native-interop failure.
    ///
    /// <c>TryCaptureChildExit</c> caught only <see cref="ObjectDisposedException"/>, so any
    /// other interop failure — the honest example being an
    /// <see cref="EntryPointNotFoundException"/> from a <c>rusty_pty</c> build that predates
    /// the <c>pty_try_get_exit_code</c> export — escaped into whichever loop had called it.
    /// The loops each have a catch-all, so the visible result was not a clean failure but a
    /// dead exit watcher and a "terminated by unhandled exception" line blaming the loop,
    /// which reads as a code regression rather than the environment problem it is.
    ///
    /// A missing export is exactly the kind of thing a session cannot fix and must not be
    /// destroyed by: the status is simply unknown, which is a state this class already
    /// handles (a native -1 means the same thing).
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class PtyChildStatusProbeFailureTests
    {
        /// The substituted probe's failure. Chosen because it is the real one: a stale native
        /// library exports every other pty_* function, so the session starts, spawns a shell
        /// and reads normally — and then fails on this one call.
        private static RustPtySession.PtyTryGetExitCodeDelegate FailingProbe(Action? onCall = null)
        {
            return (RustPtySession.PtySafeHandle handle, out int exitCode) =>
            {
                onCall?.Invoke();
                exitCode = 0;
                throw new EntryPointNotFoundException(
                    "Unable to find an entry point named 'pty_try_get_exit_code' in DLL 'rusty_pty'.");
            };
        }

        /// A read that keeps the session healthy and busy. The success branch never probes,
        /// so with this substitute every probe call is the exit watcher's own.
        private static int HealthyRead(RustPtySession.PtySafeHandle handle, byte[] buffer, int length)
        {
            Thread.Sleep(25);
            buffer[0] = (byte)'x';
            return 1;
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task AnInteropFailureDoesNotKillTheChildExitWatcher()
        {
            // The watcher polls every ChildExitPollIntervalMs for the life of the session. A
            // probe failure it cannot contain kills it on the first tick, and the session
            // silently regresses to EOF-only exit detection — the #313 bug, back again, with
            // nothing in the pane to say so.
            int probes = 0;
            using var session = NewSession(
                readFromPty: HealthyRead,
                tryGetExitCode: FailingProbe(() => Interlocked.Increment(ref probes)));

            await WaitUntilAsync(() => Volatile.Read(ref probes) >= 3, TimeSpan.FromSeconds(10));

            Assert.True(
                Volatile.Read(ref probes) >= 3,
                $"the watcher stopped polling after {Volatile.Read(ref probes)} probe(s); "
                + "a failing probe must not end the watch");
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task ARepeatedInteropFailureIsLoggedOnceNotOnEveryPoll()
        {
            // A missing export does not heal. The watcher polls every ChildExitPollIntervalMs
            // for the life of the pane, so formatting the full exception on each call turns
            // graceful degradation into ~5 error records a second, indefinitely, for one idle
            // affected pane - and the EOF resolver adds one every ChildStatusPollMs on top.
            // Report the condition once and let the watch carry on quietly.
            using var log = PtyLogCapture.Attach();

            int probes = 0;
            using var session = NewSession(
                readFromPty: HealthyRead,
                tryGetExitCode: FailingProbe(() => Interlocked.Increment(ref probes)));

            await WaitUntilAsync(() => Volatile.Read(ref probes) >= 5, TimeSpan.FromSeconds(15));

            int observedProbes = Volatile.Read(ref probes);
            Assert.True(
                observedProbes >= 5,
                $"only {observedProbes} probe(s) happened; the watcher must poll repeatedly for this to mean anything");

            Assert.Equal(1, CountOccurrences(log.ToString(), "Child status probe failed"));
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task AnInteropFailureIsLoggedAndTheEndOfStreamStillReportsAnExit()
        {
            // The other half: the status resolver polls the same probe on its way to
            // notifying the exit, and that call site sits outside ProcessLoop's try/catch —
            // so a probe that throws took the exit notification with it.
            using var log = PtyLogCapture.Attach();
            using var session = NewSession(
                readFromPty: (handle, buffer, length) => 0, // EOF immediately
                tryGetExitCode: FailingProbe());

            int? code = await WaitForExitAsync(session, TimeSpan.FromSeconds(20));

            string logText = log.ToString();

            // Unknown, so 0 — the same answer a native -1 produces, which is the point: an
            // interop failure is a status we could not learn, not a session failure.
            Assert.True(
                code == 0,
                $"expected a reported exit of 0, got {code?.ToString() ?? "no exit"}.\nPTY log:\n{logText}");

            // Logged, because "we asked and the native layer could not be called" is the one
            // piece of information that distinguishes this from an ordinary unknown status.
            Assert.Contains("Child status probe failed", logText, StringComparison.Ordinal);

            // And not misattributed to whichever loop happened to be holding the call.
            Assert.DoesNotContain("terminated by unhandled exception", logText, StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task ADisposedHandleIsStillTreatedAsTeardownRatherThanAFailure()
        {
            // ObjectDisposedException keeps its own quiet branch: it means Dispose released
            // the handle under us and owns the teardown. Widening the catch must not start
            // logging an error on every ordinary close of a pane.
            using var log = PtyLogCapture.Attach();

            using (var session = NewSession(
                readFromPty: HealthyRead,
                tryGetExitCode: (RustPtySession.PtySafeHandle handle, out int exitCode) =>
                {
                    exitCode = 0;
                    throw new ObjectDisposedException(nameof(RustPtySession.PtySafeHandle));
                }))
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            await Task.Delay(200);

            Assert.DoesNotContain("Child status probe failed", log.ToString(), StringComparison.Ordinal);
        }

        private static RustPtySession NewSession(
            RustPtySession.PtyReadDelegate readFromPty,
            RustPtySession.PtyTryGetExitCodeDelegate tryGetExitCode)
        {
            // A real shell is still spawned so the teardown acts on a real handle; only the
            // two native calls the background loops make are substituted.
            return new RustPtySession(
                ShellHelper.GetDefaultShell(),
                80,
                24,
                args: null,
                cwd: null,
                // Irrelevant here, and its SendInput would race the substituted read loop.
                skipPowerShellPostLaunchInit: true,
                environmentOverrides: null,
                readFromPty: readFromPty,
                tryGetExitCode: tryGetExitCode);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
                 i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.Elapsed < timeout)
            {
                await Task.Delay(25);
            }
        }

        /// Returns the reported exit code, or null if none arrived within the timeout. Falls
        /// back to <see cref="RustPtySession.ExitCode"/> so an exit reported before the
        /// handler was attached still counts — the loops start in the constructor.
        private static async Task<int?> WaitForExitAsync(RustPtySession session, TimeSpan timeout)
        {
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnExit(int code) => exited.TrySetResult(code);

            session.OnExit += OnExit;
            try
            {
                if (session.ExitCode.HasValue)
                {
                    return session.ExitCode;
                }

                await Task.WhenAny(exited.Task, Task.Delay(timeout));
                return exited.Task.IsCompletedSuccessfully ? exited.Task.Result : session.ExitCode;
            }
            finally
            {
                session.OnExit -= OnExit;
            }
        }
    }
}
