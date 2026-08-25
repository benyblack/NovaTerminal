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
}
