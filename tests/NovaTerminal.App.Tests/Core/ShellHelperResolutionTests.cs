using NovaTerminal.Pty;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Covers <see cref="ShellHelper.ResolveExecutableOrDefault"/>, which exists because settings and
/// session files move between machines: a workspace saved on Windows used to restore as
/// <c>cmd.exe</c> on Linux and every pane in it failed to spawn, permanently, because the command
/// was re-persisted on the way out.
/// </summary>
public sealed class ShellHelperResolutionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveExecutableOrDefault_BlankCommand_FallsBackToDefaultShell(string? command)
    {
        Assert.Equal(ShellHelper.GetDefaultShell(), ShellHelper.ResolveExecutableOrDefault(command));
    }

    [Fact]
    public void ResolveExecutableOrDefault_CommandThatCannotRunHere_FallsBackToDefaultShell()
    {
        // The whole point of the helper: non-blank but unspawnable. "cmd.exe" is the value that
        // actually shows up in session files written on Windows, and on Linux/macOS it is exactly
        // as unrunnable as an empty string was.
        string foreignShell = OperatingSystem.IsWindows() ? "/bin/bash" : "cmd.exe";

        Assert.Equal(
            ShellHelper.GetDefaultShell(),
            ShellHelper.ResolveExecutableOrDefault(foreignShell));
    }

    [Fact]
    public void ResolveExecutableOrDefault_RunnableCommand_IsLeftAlone()
    {
        // GetDefaultShell only ever returns something it found on this machine, so it doubles as
        // a known-runnable command without hardcoding a path that may not exist on the runner.
        string runnable = ShellHelper.GetDefaultShell();

        Assert.Equal(runnable, ShellHelper.ResolveExecutableOrDefault(runnable));
    }

    [Fact]
    public void ResolveExecutableOrDefault_TrimsSurroundingWhitespace()
    {
        string runnable = ShellHelper.GetDefaultShell();

        Assert.Equal(runnable, ShellHelper.ResolveExecutableOrDefault("  " + runnable + "  "));
    }

    [Fact]
    public void GetDefaultShell_ReturnsSomethingRunnable()
    {
        string shell = ShellHelper.GetDefaultShell();

        Assert.False(string.IsNullOrWhiteSpace(shell));
        Assert.True(
            File.Exists(shell) || ShellHelper.InPath(shell),
            $"GetDefaultShell() returned '{shell}', which is neither a file nor on PATH.");
    }

    // Windows resolves an extensionless command through PATHEXT - "pwsh" runs pwsh.exe - so a
    // pane whose saved command is spelled that way is perfectly runnable. Probing only the literal
    // filename called it missing, which does not merely pick another shell: SessionManager also
    // drops the pane's arguments once the command has been substituted. So the user loses the
    // command *and* its arguments, silently, on the platform most of this is developed on.
    [Fact]
    public void ResolveExecutableOrDefault_WindowsExtensionlessCommand_IsLeftAlone()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("PATHEXT resolution is Windows-only behaviour.");
        }

        // Derived from whatever this machine actually has rather than hardcoded: GetDefaultShell
        // returns something it found on PATH, and on Windows that is always a .exe.
        string shell = ShellHelper.GetDefaultShell();
        string extensionless = Path.GetFileNameWithoutExtension(shell);

        Assert.NotEqual(shell, extensionless);
        Assert.True(
            ShellHelper.InPath(extensionless),
            $"'{extensionless}' should resolve through PATHEXT, since '{shell}' is on PATH.");
        Assert.Equal(extensionless, ShellHelper.ResolveExecutableOrDefault(extensionless));
    }

    [Fact]
    public void ResolveExecutableOrDefault_QuotedCommand_IsLeftAloneAndKeepsItsQuotes()
    {
        // A configured command may be quoted to survive spaces in its path. File.Exists never
        // matches a quoted string, so this was another way to be judged unrunnable and lose the
        // arguments - and the caller wants the original spelling back, quotes included.
        string quoted = "\"" + ShellHelper.GetDefaultShell() + "\"";

        Assert.Equal(quoted, ShellHelper.ResolveExecutableOrDefault(quoted));
    }

    // A stored command may carry its arguments inline - "zsh -l", "wsl.exe -e /bin/bash" - and
    // TerminalPane.InitializeSessionCore supports exactly that, splitting executable from arguments
    // at the first space. Probing the whole string as one filename rejected commands that run
    // perfectly well, and rejection also costs the arguments.
    [Fact]
    public void ResolveExecutableOrDefault_CombinedCommandWithARunnableExecutable_IsLeftAlone()
    {
        string combined = ShellHelper.GetDefaultShell() + " -l";

        Assert.Equal(combined, ShellHelper.ResolveExecutableOrDefault(combined));
    }

    [Fact]
    public void ResolveExecutableOrDefault_CombinedCommandWhoseExecutableIsMissing_FallsBack()
    {
        // The split must not become a way to smuggle an unrunnable command past the check.
        Assert.Equal(
            ShellHelper.GetDefaultShell(),
            ShellHelper.ResolveExecutableOrDefault("definitely-not-installed-xyz -l --flag"));
    }

    [Fact]
    public void ResolveExecutableOrDefault_QuotedExecutableWithArguments_IsLeftAlone()
    {
        // The closing quote delimits the executable, not the first space - which on Windows falls
        // inside paths like "C:\\Program Files\\...".
        string combined = "\"" + ShellHelper.GetDefaultShell() + "\" -l";

        Assert.Equal(combined, ShellHelper.ResolveExecutableOrDefault(combined));
    }

    [Fact]
    public void ResolveExecutableOrDefault_PathContainingSpaces_IsFoundAsItself()
    {
        // The whole string is probed before any split, so a path that merely contains a space is
        // not mistaken for an executable plus arguments.
        string directory = Path.Combine(Path.GetTempPath(), "nova shell probe " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "my shell");

        try
        {
            WriteExecutable(executable);

            Assert.Equal(executable, ShellHelper.ResolveExecutableOrDefault(executable));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void InPath_BlankCommand_IsNotFound()
    {
        Assert.False(ShellHelper.InPath(""));
        Assert.False(ShellHelper.InPath("   "));
    }

    // Codex review, round 4a: the resolver validated the executable inside a quoted command and
    // returned the combined string, while the pane split at the first literal space and tried to
    // spawn `"C:\\Program`. Both now use ShellHelper.TrySplitCommandLine, so what gets validated is
    // what gets launched. Asserting the split - not just that the string survived - is the point:
    // the previous test only checked preservation and would have passed with the bug present.
    [Fact]
    public void TrySplitCommandLine_QuotedExecutableWithSpaces_SplitsAtTheClosingQuote()
    {
        Assert.True(ShellHelper.TrySplitCommandLine(
            "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo -File build.ps1",
            out string executable,
            out string arguments));

        Assert.Equal("C:\\Program Files\\PowerShell\\7\\pwsh.exe", executable);
        Assert.Equal("-NoLogo -File build.ps1", arguments);
    }

    [Fact]
    public void TrySplitCommandLine_QuotedExecutableWithoutArguments_IsUnquoted()
    {
        Assert.True(ShellHelper.TrySplitCommandLine("\"/opt/my shell\"", out string executable, out string arguments));

        Assert.Equal("/opt/my shell", executable);
        Assert.Equal(string.Empty, arguments);
    }

    [Fact]
    public void TrySplitCommandLine_CombinedCommand_SplitsAtTheFirstSpace()
    {
        Assert.True(ShellHelper.TrySplitCommandLine("zsh -l", out string executable, out string arguments));

        Assert.Equal("zsh", executable);
        Assert.Equal("-l", arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zsh")]
    public void TrySplitCommandLine_NothingToSplit_ReturnsFalse(string? commandLine)
    {
        Assert.False(ShellHelper.TrySplitCommandLine(commandLine, out _, out _));
    }

    [Fact]
    public void TrySplitCommandLine_AnExistingFileWithSpaces_IsNotSplit()
    {
        // "/opt/my shell" is the executable, not "/opt/my" with an argument.
        string directory = Path.Combine(Path.GetTempPath(), "nova split probe " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "my shell");

        try
        {
            WriteExecutable(executable);

            Assert.False(ShellHelper.TrySplitCommandLine(executable, out _, out _));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    // The decision behind round 4b, asserted directly. PATHEXT contains script types - .BAT, .CMD,
    // .PS1, .VBS - that only run because a shell runs them, and nothing here launches through a
    // shell: the native layer calls CreateProcessW and the fallback uses portable_pty's
    // CommandBuilder. Probing those called a batch-backed command runnable, so restore kept it and
    // its arguments and the pane failed to spawn instead of falling back to a working shell.
    //
    // Asserted as a list rather than through the filesystem on purpose: the probe is gated on
    // OperatingSystem.IsWindows, so a filesystem test passes on Linux no matter what this contains -
    // I confirmed that by putting .bat back and watching the filesystem test still pass.
    [Fact]
    public void LaunchableWindowsExtensions_ExcludeAnythingNeedingAnInterpreter()
    {
        string[] extensions = ShellHelper.LaunchableWindowsExtensions;

        Assert.Contains(".exe", extensions);

        foreach (string needsInterpreter in new[] { ".bat", ".cmd", ".ps1", ".vbs", ".js" })
        {
            Assert.DoesNotContain(needsInterpreter, extensions);
        }
    }

    // Codex review, round 4b: PATHEXT includes .BAT/.CMD, but nothing here launches through a
    // shell - the native layer calls CreateProcessW and the fallback uses portable_pty's
    // CommandBuilder, neither of which can start a batch file. Probing those extensions called such
    // a command runnable, so restore kept it and its arguments and the pane failed to spawn instead
    // of falling back to a shell that works.
    //
    // Runs on Linux too, for the same reason by a different route: no extension probing happens
    // there at all, so the extensionless name is equally unresolvable.
    [Fact]
    public void ResolveExecutableOrDefault_CommandBackedOnlyByABatchFile_IsNotConsideredRunnable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nova batch probe " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            // Only the .bat exists - no .exe, no extensionless file.
            WriteExecutable(Path.Combine(directory, "wrapper.bat"), "@echo off");
            string extensionless = Path.Combine(directory, "wrapper");

            Assert.Equal(
                ShellHelper.GetDefaultShell(),
                ShellHelper.ResolveExecutableOrDefault(extensionless));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    // Codex review, round 5a: a profile's StartingDirectory becomes the child's cwd, so a relative
    // command runs from there - while this check runs in NovaTerminal's own directory and called it
    // missing, substituting the shell and dropping the arguments.
    [Fact]
    public void ResolveExecutableOrDefault_RelativeCommandUnderTheWorkingDirectory_IsLeftAlone()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nova relative " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "tools"));

        try
        {
            WriteExecutable(Path.Combine(directory, "tools", "shell"));

            const string relative = "./tools/shell";

            // Missing when probed from here, which is the bug: nothing resolves it without the cwd.
            Assert.Equal(ShellHelper.GetDefaultShell(), ShellHelper.ResolveExecutableOrDefault(relative));

            // Found once the directory it will actually run in is taken into account.
            Assert.Equal(relative, ShellHelper.ResolveExecutableOrDefault(relative, directory));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ResolveExecutableOrDefault_RelativeCommandNotUnderTheWorkingDirectory_FallsBack()
    {
        // The working directory must not become a way to approve something that is not there.
        string directory = Path.Combine(Path.GetTempPath(), "nova relative empty " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Equal(
                ShellHelper.GetDefaultShell(),
                ShellHelper.ResolveExecutableOrDefault("./tools/shell", directory));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ResolveExecutableOrDefault_BareNameIsNotResolvedAgainstTheWorkingDirectory()
    {
        // A bare name goes through PATH, not the cwd - what a shell and exec both do. Resolving it
        // against the cwd would launch a file that merely happens to sit there.
        string directory = Path.Combine(Path.GetTempPath(), "nova bare " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            WriteExecutable(Path.Combine(directory, "definitely-not-on-path-xyz"));

            Assert.Equal(
                ShellHelper.GetDefaultShell(),
                ShellHelper.ResolveExecutableOrDefault("definitely-not-on-path-xyz", directory));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    // Codex review, round 5b: restricting only the *appended* extensions left an explicit
    // wrapper.bat passing File.Exists and being kept, which is the dead pane the restriction was
    // meant to prevent. Asserted on the policy, since the caller is gated on Windows.
    [Theory]
    [InlineData("C:\\tools\\wrapper.bat")]
    [InlineData("C:\\tools\\wrapper.cmd")]
    [InlineData("wrapper.BAT")]
    [InlineData("script.ps1")]
    [InlineData("script.vbs")]
    public void NeedsAnInterpreter_ScriptFiles_AreRecognised(string candidate)
    {
        Assert.True(ShellHelper.NeedsAnInterpreter(candidate));
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("/bin/bash")]
    [InlineData("zsh")]
    [InlineData("python3.11")]
    [InlineData(null)]
    [InlineData("")]
    public void NeedsAnInterpreter_ExecutablesAndBareNames_AreNot(string? candidate)
    {
        Assert.False(ShellHelper.NeedsAnInterpreter(candidate));
    }

    [Fact]
    public void TheTwoExtensionPoliciesDoNotOverlap()
    {
        foreach (string launchable in ShellHelper.LaunchableWindowsExtensions)
        {
            Assert.DoesNotContain(launchable, ShellHelper.WindowsExtensionsNeedingAnInterpreter);
        }
    }

    // Codex review, round 6: the implicit-extension probe modelled a shell, not the launcher.
    // CreateProcessW with lpApplicationName NULL (native/src/lib.rs:301) appends .exe and only when
    // the name has no extension at all - so a saved `tool` backed by `tool.com`, and a saved
    // `python3.11` backed by `python3.11.exe`, are both unreachable, yet the probe called them
    // runnable and the pane was kept instead of falling back.
    [Theory]
    [InlineData("tool")]
    [InlineData("pwsh")]
    [InlineData("C:\\tools\\wrapper")]
    public void AppendsImplicitExtension_NamesWithoutAnExtension_AreProbed(string candidate)
    {
        Assert.True(ShellHelper.AppendsImplicitExtension(candidate));
    }

    [Theory]
    // A dot anywhere in the final component counts as an extension to CreateProcessW, however
    // little it looks like one.
    [InlineData("python3.11")]
    [InlineData("tool.com")]
    [InlineData("pwsh.exe")]
    [InlineData("wrapper.bat")]
    [InlineData(null)]
    [InlineData("")]
    public void AppendsImplicitExtension_NamesThatAlreadyHaveOne_AreNot(string? candidate)
    {
        Assert.False(ShellHelper.AppendsImplicitExtension(candidate));
    }

    [Fact]
    public void LaunchableWindowsExtensions_IsOnlyWhatTheLauncherAppends()
    {
        // .exe and nothing else: CreateProcessW does not append .com, and PATHEXT is a shell's list.
        Assert.Equal(new[] { ".exe" }, ShellHelper.LaunchableWindowsExtensions);
    }

    // The policy tests above pin the decision; this pins that it is actually applied, which no test
    // on Linux can observe because the probe is gated on Windows. Runs on the Windows jobs.
    [Fact]
    public void ResolveExecutableOrDefault_DottedNameBackedByAnExe_FallsBackOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Implicit extension probing is Windows-only behaviour.");
        }

        string directory = Path.Combine(Path.GetTempPath(), "nova dotted " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            // CreateProcessW sees "python3.11" as already extensioned and never looks for this file.
            WriteExecutable(Path.Combine(directory, "python3.11.exe"));

            Assert.Equal(
                ShellHelper.GetDefaultShell(),
                ShellHelper.ResolveExecutableOrDefault(Path.Combine(directory, "python3.11")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ResolveExecutableOrDefault_ExtensionlessNameBackedByAnExe_ResolvesOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Implicit extension probing is Windows-only behaviour.");
        }

        string directory = Path.Combine(Path.GetTempPath(), "nova implicit " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            WriteExecutable(Path.Combine(directory, "tool.exe"));
            string extensionless = Path.Combine(directory, "tool");

            // The flip side, so the narrowing above cannot be over-applied.
            Assert.Equal(extensionless, ShellHelper.ResolveExecutableOrDefault(extensionless));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    // Codex review, round 7: an existing file is not a spawnable one on Unix. The tests above used
    // to create their probe files with File.WriteAllText - mode 0644 - and assert they resolved,
    // so they were pinning the bug in place. They now write genuinely executable files, and this
    // covers the case they were accidentally asserting.
    [Fact]
    public void ResolveExecutableOrDefault_FileWithoutAnExecuteBit_FallsBack()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Execute bits are a Unix concept.");
        }

        string directory = Path.Combine(Path.GetTempPath(), "nova noexec " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string notExecutable = Path.Combine(directory, "shell");
            File.WriteAllText(notExecutable, "");
            File.SetUnixFileMode(
                notExecutable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            Assert.Equal(ShellHelper.GetDefaultShell(), ShellHelper.ResolveExecutableOrDefault(notExecutable));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    // Codex review, round 7: CreateProcessW resolves a relative executable against the *calling*
    // process's directory, so a match under the child's working directory means nothing on Windows -
    // approving on that basis keeps a command whose spawn then fails.
    [Fact]
    public void ResolveExecutableOrDefault_RelativeCommand_IsOnlyResolvedFromTheWorkingDirectoryOnUnix()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nova relwin " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "tools"));

        try
        {
            WriteExecutable(Path.Combine(directory, "tools", "shell"));

            string resolved = ShellHelper.ResolveExecutableOrDefault("./tools/shell", directory);

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(ShellHelper.GetDefaultShell(), resolved);
            }
            else
            {
                Assert.Equal("./tools/shell", resolved);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Writes a file that is actually executable, so a probe of it means something.</summary>
    private static void WriteExecutable(string path, string contents = "")
    {
        File.WriteAllText(path, contents);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
