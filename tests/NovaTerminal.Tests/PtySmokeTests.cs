using System;
using System.IO;
using System.Threading.Tasks;
using NovaTerminal.Core;
using NovaTerminal.VT;
using Xunit;

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
