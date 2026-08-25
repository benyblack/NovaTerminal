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

    [Fact]
    public void InPath_BlankCommand_IsNotFound()
    {
        Assert.False(ShellHelper.InPath(""));
        Assert.False(ShellHelper.InPath("   "));
    }

    // The PATHEXT parsing runs only on Windows, so these cover it from here - the surrounding
    // extension handling is a File.Exists loop, but this is the part with details worth getting
    // wrong, and it needs no Windows filesystem to check.
    [Theory]
    [InlineData(".COM;.EXE;.BAT", new[] { ".COM", ".EXE", ".BAT" })]
    [InlineData(".EXE", new[] { ".EXE" })]
    // Entries without a leading dot, which PATHEXT is not required to have.
    [InlineData("EXE;BAT", new[] { ".EXE", ".BAT" })]
    // Stray whitespace and empty entries from a hand-edited variable.
    [InlineData(".EXE; .BAT ;;.CMD", new[] { ".EXE", ".BAT", ".CMD" })]
    public void ParseExecutableExtensions_NormalisesTheValue(string pathExt, string[] expected)
    {
        Assert.Equal(expected, ShellHelper.ParseExecutableExtensions(pathExt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseExecutableExtensions_WhenPathExtIsUnset_FallsBackToTheLauncherDefaults(string? pathExt)
    {
        string[] extensions = ShellHelper.ParseExecutableExtensions(pathExt);

        // An extensionless "pwsh" has to keep resolving even where PATHEXT has been cleared.
        Assert.Contains(".EXE", extensions);
        Assert.Contains(".COM", extensions);
        Assert.Contains(".BAT", extensions);
        Assert.Contains(".CMD", extensions);
    }
}
