using NovaTerminal.Pty;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Covers <see cref="ShellHelper.ResolveExecutableOrDefault"/> and the portability predicate behind
/// it, which exist because settings and session files move between machines: a workspace saved on
/// Windows restored as <c>cmd.exe</c> on Linux and every pane in it failed to spawn, permanently,
/// because the failing command was captured again on the way out.
/// </summary>
/// <remarks>
/// These tests deliberately do not probe the filesystem. An earlier version of the predicate tried
/// to decide launchability in general - PATHEXT, implicit extensions, execute bits, an interpreter
/// list, working directories - and its tests grew to match, including several that asserted the bug
/// rather than the behaviour. The question is now only whether a command was written for another
/// operating system, which is answerable from the name.
/// </remarks>
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

    // The bug this all started from: a Windows workspace opened on Linux.
    [Fact]
    public void ResolveExecutableOrDefault_CommandFromAnotherPlatform_FallsBackToDefaultShell()
    {
        string foreign = OperatingSystem.IsWindows() ? "/bin/bash" : "cmd.exe";

        Assert.Equal(ShellHelper.GetDefaultShell(), ShellHelper.ResolveExecutableOrDefault(foreign));
    }

    // Everything that is not addressed to another OS is handed back untouched, including the shapes
    // the old launchability predicate kept getting wrong. None of these need a filesystem lookup.
    [Theory]
    [InlineData("zsh")]
    [InlineData("pwsh")]
    [InlineData("pwsh -NoLogo")]
    [InlineData("./tools/shell")]
    [InlineData("tools/shell --flag")]
    [InlineData("some-tool-that-is-not-installed")]
    public void ResolveExecutableOrDefault_CommandForThisPlatform_IsLeftAlone(string command)
    {
        Assert.Equal(command, ShellHelper.ResolveExecutableOrDefault(command));
    }

    // A rooted path only Windows understands. Verified on Windows: a leading slash there means
    // rooted-on-the-current-drive and /Windows/System32/cmd.exe genuinely launches, so the rule can
    // no longer be "starts with a slash" - it has to name a root only Unix has.
    [Fact]
    public void IsCommandForAnotherPlatform_DriveRelativeWindowsPath_IsNotForeignOnWindows()
    {
        Assert.Equal(
            !OperatingSystem.IsWindows(),
            ShellHelper.IsCommandForAnotherPlatform("/Windows/System32/cmd.exe"));
    }

    /// <summary>A path shaped for the platform the test is running on, which does not exist.</summary>
    private static string NativeShapedMissingCommand =>
        OperatingSystem.IsWindows()
            ? @"C:\nope\definitely-not-installed-xyz.exe"
            : "/opt/definitely-not-installed-xyz/shell";

    [Fact]
    public void ResolveExecutableOrDefault_TrimsSurroundingWhitespace()
    {
        Assert.Equal("zsh", ShellHelper.ResolveExecutableOrDefault("  zsh  "));
    }

    [Fact]
    public void ResolveExecutableOrDefault_QuotedCommand_KeepsItsQuotes()
    {
        // Whoever spawns it wants the original spelling; TrySplitCommandLine unquotes at the point
        // of use. Native-shaped for the same reason as above.
        string quoted = OperatingSystem.IsWindows()
            ? "\"C:\\my dir\\shell.exe\" -l"
            : "\"/opt/my shell\" -l";

        Assert.Equal(quoted, ShellHelper.ResolveExecutableOrDefault(quoted));
    }

    // A missing command is deliberately NOT substituted. The user asked for their shell, not for a
    // shell, and a substitution is silent - so a pane that fails to spawn, which they can see and
    // diagnose, is the better outcome than one quietly running something else.
    [Fact]
    public void ResolveExecutableOrDefault_CommandThatSimplyDoesNotExist_IsStillLeftAlone()
    {
        // Native-shaped on purpose. A POSIX path here would be correctly judged foreign on Windows
        // and substituted, so the test would fail for a reason that has nothing to do with what it
        // is checking - which is exactly how this file broke the Windows run twice.
        string missing = NativeShapedMissingCommand;

        Assert.Equal(missing, ShellHelper.ResolveExecutableOrDefault(missing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCommandForAnotherPlatform_Blank_IsNotForeign(string? command)
    {
        Assert.False(ShellHelper.IsCommandForAnotherPlatform(command));
    }

    // Windows-only shapes. Asserted from Linux, where they are foreign; on Windows they are native
    // and the expectation flips, which the theory below covers.
    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("powershell.exe")]
    [InlineData("wsl.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:/Windows/System32/cmd.exe")]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData("wrapper.bat")]
    [InlineData("wrapper.cmd")]
    [InlineData("script.ps1")]
    // Arguments are ignored - the executable is what identifies the platform.
    [InlineData("cmd.exe /c echo hi")]
    [InlineData(@"""C:\Program Files\Tool\tool.exe"" --flag")]
    public void IsCommandForAnotherPlatform_WindowsShapes(string command)
    {
        Assert.Equal(!OperatingSystem.IsWindows(), ShellHelper.IsCommandForAnotherPlatform(command));
    }

    // Unix-only shapes: a rooted POSIX path. The mirror of the theory above.
    [Theory]
    [InlineData("/bin/bash")]
    [InlineData("/usr/bin/zsh")]
    [InlineData("/bin/bash --norc -i")]
    public void IsCommandForAnotherPlatform_UnixShapes(string command)
    {
        Assert.Equal(OperatingSystem.IsWindows(), ShellHelper.IsCommandForAnotherPlatform(command));
    }

    // Neither platform's marker, so never foreign on either. pwsh matters most here: PowerShell is
    // cross-platform, and an earlier draft that keyed off shell names alone would have rejected it
    // on Linux, where it is a perfectly good command.
    [Theory]
    [InlineData("pwsh")]
    [InlineData("zsh")]
    [InlineData("bash")]
    [InlineData("./tools/shell")]
    [InlineData("tools/shell")]
    [InlineData("python3.11")]
    public void IsCommandForAnotherPlatform_PortableShapes_AreNeverForeign(string command)
    {
        Assert.False(ShellHelper.IsCommandForAnotherPlatform(command));
    }

    // Batch files run fine on Windows - CreateProcess hands them to the command processor, verified
    // there against a raw CreateProcessW P/Invoke and through RustPtySession itself. An earlier
    // version of this code rejected them as unlaunchable and substituted the default shell for a
    // wrapper that had been working.
    [Fact]
    public void IsCommandForAnotherPlatform_BatchFileOnWindows_IsNative()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("A .bat is foreign on Unix; this pins that it is not foreign on Windows.");
        }

        Assert.False(ShellHelper.IsCommandForAnotherPlatform(@"C:\tools\wrapper.bat"));
        Assert.Equal(@"C:\tools\wrapper.bat", ShellHelper.ResolveExecutableOrDefault(@"C:\tools\wrapper.bat"));
    }

    [Fact]
    public void TrySplitCommandLine_QuotedExecutableWithSpaces_SplitsAtTheClosingQuote()
    {
        Assert.True(ShellHelper.TrySplitCommandLine(
            "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo -File build.ps1",
            out string executable,
            out string arguments));

        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", executable);
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
        // "/opt/my shell" is the executable, not "/opt/my" with an argument. This guard is
        // load-bearing: the app persists a resolved command unquoted, so a path with spaces comes
        // back through here on every session restore.
        string directory = Path.Combine(Path.GetTempPath(), "nova split probe " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "my shell");

        try
        {
            File.WriteAllText(executable, "");

            Assert.False(ShellHelper.TrySplitCommandLine(executable, out _, out _));
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

    [Fact]
    public void GetDefaultShell_ReturnsSomethingRunnable()
    {
        string shell = ShellHelper.GetDefaultShell();

        Assert.False(string.IsNullOrWhiteSpace(shell));
        Assert.True(
            File.Exists(shell) || ShellHelper.InPath(shell),
            $"GetDefaultShell() returned '{shell}', which is neither a file nor on PATH.");
    }

    // InPath keeps its Windows extension-awareness, which is not about portability: it fixes a
    // pre-existing defect in the settings and palette checks, where an extensionless profile command
    // was judged missing. Verified on Windows - `pwsh` resolves via `pwsh.exe`.
    [Fact]
    public void InPath_ExtensionlessCommand_ResolvesThroughTheImplicitExeOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Implicit .exe resolution is Windows-only behaviour.");
        }

        string shell = ShellHelper.GetDefaultShell();
        string extensionless = Path.GetFileNameWithoutExtension(shell);

        Assert.NotEqual(shell, extensionless);
        Assert.True(
            ShellHelper.InPath(extensionless),
            $"'{extensionless}' should resolve through the implicit .exe, since '{shell}' is on PATH.");
    }
}
