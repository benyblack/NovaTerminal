using NovaTerminal.Core;

namespace NovaTerminal.Tests;

public sealed class OscShellIntegrationTests
{
    [Fact]
    public void Osc133B_RaisesCommandStarted()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        bool started = false;

        parser.OnCommandStarted += () => started = true;
        parser.Process("\x1b]133;B\x07");

        Assert.True(started);
    }

    [Fact]
    public void Osc133D_WithExitCode_RaisesCommandFinished()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        int? exitCode = null;

        parser.OnCommandFinished += code => exitCode = code;
        parser.Process("\x1b]133;D;17\x07");

        Assert.Equal(17, exitCode);
    }

    [Fact]
    public void Osc133D_WithoutExitCode_RaisesCommandFinishedWithNull()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        bool fired = false;
        int? exitCode = 0;

        parser.OnCommandFinished += code =>
        {
            fired = true;
            exitCode = code;
        };

        parser.Process("\x1b]133;D\x07");

        Assert.True(fired);
        Assert.Null(exitCode);
    }

    [Fact]
    public void Osc133A_RaisesPromptReady()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        bool fired = false;

        parser.OnPromptReady += () => fired = true;
        parser.Process("\x1b]133;A\x07");

        Assert.True(fired);
    }

    [Fact]
    public void Osc133C_WithBase64Command_RaisesCommandAccepted()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        string? acceptedCommand = null;

        parser.OnCommandAccepted += command => acceptedCommand = command;
        parser.Process("\x1b]133;C;Z2l0IHN0YXR1cw==\x07");

        Assert.Equal("git status", acceptedCommand);
    }

    [Fact]
    public void Osc133D_WithExitCodeAndDuration_RaisesDetailedCommandFinished()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        (int? ExitCode, long? DurationMs) result = default;

        parser.OnCommandFinishedDetailed += (exitCode, durationMs) => result = (exitCode, durationMs);
        parser.Process("\x1b]133;D;17;2450\x07");

        Assert.Equal(17, result.ExitCode);
        Assert.Equal(2450, result.DurationMs);
    }
}
