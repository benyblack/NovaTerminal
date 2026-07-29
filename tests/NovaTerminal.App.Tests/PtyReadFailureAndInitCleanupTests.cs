using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Pty;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Regression guards for #107.
    ///
    /// (a) A negative <c>pty_read</c> used to reset the decoder and sleep 50 ms forever,
    /// so a permanently failing handle span at 20 Hz for the life of the process while the
    /// tab sat frozen with no error. It is now bounded and reports a distinct exit code.
    ///
    /// (b) The PowerShell post-launch init wrote <c>nova_init_{guid}.ps1</c> into %TEMP%
    /// and never deleted it, leaking one file per PowerShell session.
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class PtyReadFailureAndInitCleanupTests
    {
        [Fact]
        public void ReadFailureExitCode_IsDistinguishableFromACleanExit()
        {
            // The whole point of the code is to tell "the shell exited" apart from "we
            // gave up reading". If this ever became 0 the distinction would vanish
            // silently, with no test failing anywhere else.
            Assert.NotEqual(0, RustPtySession.ReadFailureExitCode);
        }

        [Fact]
        public void MaxConsecutiveReadErrors_IsBoundedAndAllowsForTransientFailures()
        {
            // Bounded at all: an unbounded value would restore the infinite spin.
            Assert.InRange(RustPtySession.MaxConsecutiveReadErrors, 1, 1000);

            // And not so small that a brief transient blip kills a healthy session.
            Assert.True(
                RustPtySession.MaxConsecutiveReadErrors >= 5,
                "too few retries would tear down a session over a momentary read failure");
        }

        // NOTE: an "EOF still reports exit code 0" test was written and then removed here.
        // Driving a real shell to exit by sending "exit\r" did not reliably produce an
        // OnExit within 15s on Windows — the shell can still be busy with the post-launch
        // init injection when the input lands, so the test was timing-dependent rather
        // than assertion-dependent. A flaky guard is worse than an honest gap.
        //
        // Neither side of the exit-code change (EOF => 0, repeated read failure =>
        // ReadFailureExitCode) is directly covered as a result: forcing a negative
        // pty_read needs a seam that does not exist, since Native.pty_read is a static
        // P/Invoke. Making the read callable injectable would give both cases real
        // coverage and is worth doing, but it is a refactor beyond this fix.

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task PowerShellInit_LeavesNoTempScriptBehind()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // The init injection is PowerShell-only, so there is nothing to leak.
                return;
            }

            var session = new RustPtySession("powershell.exe", 80, 24);
            string? scriptPath;
            try
            {
                // The injection fires on a 300 ms delay; wait past it so the script is
                // actually written and sourced rather than skipped by cancellation.
                await Task.Delay(2500);
                scriptPath = session.PowerShellInitScriptPath;

                Assert.NotNull(scriptPath);
            }
            finally
            {
                session.Dispose();
            }

            // The script deletes itself once sourced, and Dispose removes it if the shell
            // never ran it. Either way, the file this session created must not survive.
            Assert.False(
                File.Exists(scriptPath),
                $"PowerShell init script survived disposal: {scriptPath}");
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task DisposeDuringInitDelay_LeavesNoTempScriptAndDoesNotHang()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var session = new RustPtySession("powershell.exe", 80, 24);

            // Dispose *inside* the 300 ms injection window: the task must observe
            // cancellation rather than inject into a dead handle, and Dispose must not
            // block waiting for it.
            await Task.Delay(50);

            var sw = Stopwatch.StartNew();
            session.Dispose();
            sw.Stop();

            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(5),
                $"Dispose took {sw.Elapsed.TotalSeconds:F1}s — it should not block on the init task");

            // Cancelled before the write, so no script should ever have been created; if
            // one was, it must not have survived.
            await Task.Delay(750);
            string? scriptPath = session.PowerShellInitScriptPath;

            Assert.False(
                scriptPath != null && File.Exists(scriptPath),
                $"cancelled init left a script behind: {scriptPath}");
        }
    }
}
