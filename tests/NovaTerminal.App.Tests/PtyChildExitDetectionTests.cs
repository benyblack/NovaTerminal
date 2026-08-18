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
    /// #313: a session must notice that its shell has ended.
    ///
    /// Pipe EOF cannot carry that on Windows. With ConPTY the output pipe stays open for as
    /// long as the console host lives, and the host outlives its client shell — measured at
    /// 159s and counting after a shell had exited. A session that waited only for EOF
    /// therefore never learned that a shell was gone: it only learned that a host had
    /// *crashed*. That is why the pane in #311's bug report sat there looking alive, and why
    /// #311's banner never appeared in a real build even with every headless test passing —
    /// those tests raise the exit event directly, so they cover everything downstream of it
    /// and nothing upstream.
    ///
    /// These tests drive real shells and assert on the event itself, which is the piece that
    /// was missing.
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class PtyChildExitDetectionTests
    {
        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task ACleanExitIsReportedWithCodeZero()
        {
            using var session = NewSession();
            await WaitForPromptAsync(session);

            using var log = PtyLogCapture.Attach();
            session.SendInput("exit\r");

            int? code = await WaitForExitAsync(session, TimeSpan.FromSeconds(30));

            // The log is attached because the interesting failure is a wrong *code*, not a
            // missing exit: under parallel load this reported a non-zero code once, and
            // "Assert.Equal(0, code)" alone cannot tell you whether the shell died early,
            // the read loop claimed the exit first, or `exit` landed mid-startup.
            Assert.True(code == 0, $"expected a clean 0, got {code?.ToString() ?? "no exit"}.\nPTY log:\n{log}");
            Assert.False(session.IsProcessRunning, "the session must not report itself as running once its shell has gone");
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task TheShellsRealExitCodeIsReported()
        {
            // The assertion that #313 is really about: not "an exit happened" but "the exit
            // carried the shell's own status". Before the fix every local session reported 0,
            // so a crashed console host and a deliberate `exit` were indistinguishable.
            // 42 is arbitrary and understood by cmd, pwsh and sh alike.
            using var session = NewSession();
            await WaitForPromptAsync(session);

            session.SendInput("exit 42\r");

            int? code = await WaitForExitAsync(session, TimeSpan.FromSeconds(30));

            Assert.Equal(42, code);
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task AKilledShellIsReportedAsExited()
        {
            using var session = NewSession();
            await WaitForPromptAsync(session);

            int pid = session.Pid ?? throw new InvalidOperationException("the session reported no pid");
            using (Process shell = Process.GetProcessById(pid))
            {
                shell.Kill(entireProcessTree: true);
            }

            int? code = await WaitForExitAsync(session, TimeSpan.FromSeconds(30));

            // What this test is for: the session notices, rather than sitting there believing a
            // dead shell is alive. The *value* is asserted by TheShellsRealExitCodeIsReported,
            // which uses `exit 42` and is meaningful on every platform.
            //
            // The code deliberately is not asserted cross-platform, and the first version of
            // this test was wrong to try (it asserted non-zero and failed on ubuntu CI):
            //   * Unix reports a signal death with WEXITSTATUS 0, so a SIGKILL'd shell
            //     legitimately surfaces as 0 - indistinguishable here from a clean exit.
            //   * On Windows .NET's Process.Kill terminates with -1, which also happens to be
            //     RustPtySession.ReadFailureExitCode, so a managed-tool kill is
            //     indistinguishable from "this session's reads failed". That collision predates
            //     this test and means the same thing to a user either way.
            Assert.NotNull(code);
            Assert.False(session.IsProcessRunning, "a session whose shell was killed must not report itself as running");

            if (OperatingSystem.IsWindows())
            {
                // Windows does carry a distinct status for a terminated process, so hold it to
                // that: reporting a clean 0 here would be the old "assumed 0" bug returning.
                Assert.NotEqual(0, code!.Value);
            }
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task ALiveShellIsNotReportedAsExited()
        {
            // The other half: the watcher must not manufacture an exit for a healthy session,
            // or every pane would announce itself dead a fraction of a second after opening.
            using var session = NewSession();
            await WaitForPromptAsync(session);

            int? code = await WaitForExitAsync(session, TimeSpan.FromSeconds(3));

            Assert.Null(code);
            Assert.True(session.IsProcessRunning, "a shell sitting at its prompt is still running");
        }

        /// Tees <see cref="NovaTerminal.Pty.PtyLogger"/> into a string for the duration of a
        /// test, so an assertion can attach what the session actually did. These sessions
        /// drive real shells, and their failures are timing-shaped: without the log a failure
        /// tells you the code was wrong and nothing about why.
        private sealed class PtyLogCapture : IDisposable
        {
            private readonly System.Text.StringBuilder _log = new();
            private readonly Action<NovaTerminal.Pty.PtyLogLevel, string>? _previous;

            private PtyLogCapture()
            {
                _previous = NovaTerminal.Pty.PtyLogger.Sink;
                NovaTerminal.Pty.PtyLogger.Sink = (level, message) =>
                {
                    lock (_log) { _log.AppendLine($"[{level}] {message}"); }
                    _previous?.Invoke(level, message);
                };
            }

            public static PtyLogCapture Attach() => new();

            public void Dispose() => NovaTerminal.Pty.PtyLogger.Sink = _previous;

            public override string ToString()
            {
                lock (_log) { return _log.ToString(); }
            }
        }

        private static RustPtySession NewSession()
        {
            return new RustPtySession(
                ShellHelper.GetDefaultShell(),
                80,
                24,
                args: null,
                cwd: null,
                // The injection would race our own SendInput and is irrelevant here.
                skipPowerShellPostLaunchInit: true);
        }

        /// Waits until the shell has produced output, so `exit` is typed at a shell that is
        /// actually reading. Without this the test races shell startup.
        private static async Task WaitForPromptAsync(RustPtySession session)
        {
            var sawOutput = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOutput(string _) => sawOutput.TrySetResult(true);

            session.OnOutputReceived += OnOutput;
            try
            {
                await Task.WhenAny(sawOutput.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            }
            finally
            {
                session.OnOutputReceived -= OnOutput;
            }

            // The prompt is drawn a beat after the first bytes; a short settle keeps the
            // typed command from landing mid-startup.
            await Task.Delay(500);
        }

        /// Returns the reported exit code, or null if none arrived within the timeout.
        /// Falls back to <see cref="RustPtySession.ExitCode"/> so a fast exit reported
        /// before the handler is attached still counts.
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
