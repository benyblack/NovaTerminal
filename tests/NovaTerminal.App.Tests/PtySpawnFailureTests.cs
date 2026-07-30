using System;
using NovaTerminal.Pty;
using Xunit;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// #120 item 3: a failed spawn used to throw a bare "Failed to create Rust PTY session." — the
    /// native layer returned a null pointer and nothing else, so a missing shell binary, a deleted
    /// working directory and an openpty failure were indistinguishable. That is the single most
    /// useful thing to know when a tab refuses to open.
    ///
    /// In this collection because it spawns through the real native library.
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class PtySpawnFailureTests
    {
        private const string MissingShell = "novaterminal-no-such-shell-3d81ac.exe";

        [Fact]
        public void Spawning_a_missing_shell_reports_the_command_in_the_exception()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => new RustPtySession(MissingShell, cols: 80, rows: 24));

            // Naming the shell is the point: the old message named nothing at all.
            Assert.Contains(MissingShell, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Spawning_a_missing_shell_reports_the_native_reason()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => new RustPtySession(MissingShell, cols: 80, rows: 24));

            // The native message is appended after a colon. Assert that *something* came across
            // the pty_last_error channel rather than matching OS-specific wording: the underlying
            // text differs between Windows ("The system cannot find the file specified") and Unix
            // ("No such file or directory"), and pinning either would make this a Windows-only
            // test in disguise.
            int separator = ex.Message.IndexOf("': ", StringComparison.Ordinal);
            Assert.True(
                separator > 0,
                $"expected a native reason appended to the message, got: {ex.Message}");
            Assert.NotEmpty(ex.Message.Substring(separator + 3).Trim());
        }
    }
}
