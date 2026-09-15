using System;
using System.Collections.Generic;
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

        /// <summary>
        /// An empty command must be refused here, not by the OS.
        /// </summary>
        /// <remarks>
        /// Reported symptom: a pane showed
        /// <c>Failed to create Rust PTY session for '': failed to spawn '': CreateProcessW
        /// `"C:\Program Files (x86)\VMware\VMware Workstation\bin\\"` ... Access is denied.
        /// (os error 5)</c>. Nothing in the app ever mentioned VMware — portable-pty resolves a
        /// program name by joining it onto each %PATH% entry and taking the first candidate that
        /// exists, and joining "" onto a directory is that directory. So the empty command became
        /// "execute the first folder on %PATH%", and the OS's refusal named a path the user had
        /// nothing to do with. Every minute spent on that message was wasted.
        /// </remarks>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void Spawning_an_empty_command_fails_with_a_message_about_the_empty_command(string blank)
        {
            var ex = Assert.Throws<ArgumentException>(
                () => new RustPtySession(blank, cols: 80, rows: 24));

            Assert.Contains(RustPtySession.EmptyCommandMessage, ex.Message, StringComparison.Ordinal);
            // The failure the old code produced. If it ever comes back, it comes back from the OS
            // with this in it.
            Assert.DoesNotContain("Access is denied", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CreateProcessW", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A NUL cannot be caught on the Rust side: every string here is marshalled as a
        /// NUL-terminated C string and read back with <c>CStr::from_ptr</c>, which stops at the
        /// first NUL. Left unchecked, the child is silently spawned from a prefix of what the
        /// caller asked for — so the check has to happen while the whole string is still visible.
        /// </summary>
        [Fact]
        public void Spawning_a_command_with_an_embedded_nul_is_rejected_rather_than_truncated()
        {
            // Truncation would make this "cmd.exe" and spawn a perfectly good shell — the
            // dangerous outcome, because it silently runs something other than what was asked.
            var ex = Assert.Throws<ArgumentException>(
                () => new RustPtySession("cmd.exe\0-rm-rf", cols: 80, rows: 24));

            Assert.Contains("NUL", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Spawn_arguments_with_an_embedded_nul_are_rejected()
        {
            Assert.Throws<ArgumentException>(
                () => new RustPtySession("cmd.exe", cols: 80, rows: 24, args: "/c echo\0hi"));
        }

        [Fact]
        public void A_working_directory_with_an_embedded_nul_is_rejected()
        {
            Assert.Throws<ArgumentException>(
                () => new RustPtySession("cmd.exe", cols: 80, rows: 24, cwd: "C:\\Users\0"));
        }

        [Fact]
        public void An_environment_override_with_an_embedded_nul_is_rejected()
        {
            var overrides = new Dictionary<string, string> { ["ZDOTDIR"] = "/tmp\0/evil" };

            Assert.Throws<ArgumentException>(
                () => new RustPtySession(
                    "cmd.exe",
                    cols: 80,
                    rows: 24,
                    environmentOverrides: overrides));
        }
    }
}
