using NovaTerminal.Shell;
using System;
using System.IO;
using System.Threading.Tasks;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using Xunit;
using NovaTerminal.Pty;

namespace NovaTerminal.Tests
{
    public class PtySmokeTests
    {
        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task RustPtySession_CanSpawnResizeWriteAndDispose()
        {
            string shell = ShellHelper.GetDefaultShell();
            using var session = new RustPtySession(shell, 80, 24);

            // Basic lifecycle and native interop sanity checks.
            await Task.Delay(250);
            session.Resize(100, 30);
            session.SendInput("\r");
            await Task.Delay(150);
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task RustPtySession_FlightRecording_CapturesOutputAndExports()
        {
            string shell = ShellHelper.GetDefaultShell();
            string tempFile = Path.GetTempFileName();
            try
            {
                using var session = new RustPtySession(shell, 80, 24);

                // Off by default: no ring, export refuses without touching the file.
                Assert.False(session.IsFlightRecording);
                Assert.False(session.TryExportFlightRecording(tempFile, out _));

                session.EnableFlightRecording(2 * 1024 * 1024);
                Assert.True(session.IsFlightRecording);

                // The shell banner/prompt guarantees some output; poll until the
                // ring has captured at least one chunk.
                NovaTerminal.Replay.FlightExportInfo info = default;
                bool exported = false;
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(250);
                    exported = session.TryExportFlightRecording(tempFile, out info);
                    Assert.True(exported);
                    if (info.EventCount > 0) break;
                }

                Assert.True(exported);
                Assert.True(info.EventCount > 0, "flight ring captured no output from a live shell");
                Assert.True(File.Exists(tempFile));

                // Try-pattern: an unwritable path is reported as false, not thrown.
                string badPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nested", "export.rec");
                Assert.False(session.TryExportFlightRecording(badPath, out _));
                Assert.True(session.IsFlightRecording); // failure does not disturb the ring

                // Disable drops the ring.
                session.DisableFlightRecording();
                Assert.False(session.IsFlightRecording);
                Assert.False(session.TryExportFlightRecording(tempFile, out _));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task RustPtySession_CanSpawnExecutableWithSpacesInPath()
        {
            // Regression: CreateProcessW with lpApplicationName=NULL and an
            // unquoted "C:\Program Files\..." cmdline fails to disambiguate
            // the exe boundary, so pwsh at its default Program Files install
            // would silently fail to start. The Rust spawn helper now wraps
            // a whitespace-containing exe in quotes before concatenating
            // args. Use Git Bash on Windows since it's a known
            // Program-Files-path executable that ships on most dev boxes.
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            string[] candidates =
            {
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files\Git\usr\bin\bash.exe",
                @"C:\Program Files\PowerShell\7\pwsh.exe",
            };

            string? shell = null;
            foreach (string c in candidates)
            {
                if (File.Exists(c)) { shell = c; break; }
            }

            if (shell is null)
            {
                // None of the spacey-path candidates are installed on this
                // box; nothing to regress against.
                return;
            }

            Assert.Contains(' ', shell);

            using var session = new RustPtySession(shell, 80, 24);
            await Task.Delay(250);
            session.SendInput("\r");
            await Task.Delay(150);
        }
    }
}
