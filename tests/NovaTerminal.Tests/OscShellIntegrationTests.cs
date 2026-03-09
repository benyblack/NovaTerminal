using System.Text;
using System.Linq;
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
    public void Osc133C_WithMultilineBase64Command_RaisesCommandAccepted()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        string? acceptedCommand = null;
        const string command = "foreach ($i in 1..3) {\r\n    Write-Output $i\r\n}";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        parser.OnCommandAccepted += command => acceptedCommand = command;
        parser.Process($"\x1b]133;C;{encoded}\x07");

        Assert.Equal(command, acceptedCommand);
    }

    [Fact]
    public void Osc133C_SplitAcrossProcessCalls_RaisesCommandAcceptedWithoutLeakingPadding()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        string? acceptedCommand = null;

        parser.OnCommandAccepted += command => acceptedCommand = command;
        parser.Process("\x1b]133;C;Z2l0IHN0");
        parser.Process("YXR1cw==\x07");

        Assert.Equal("git status", acceptedCommand);
        Assert.Equal(string.Empty, GetVisiblePlainText(buffer).Trim());
    }

    [Fact]
    public void Osc133A_FollowedImmediatelyByPromptText_DoesNotCorruptPrompt()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b]133;A\x07PS C:\\repo> ");

        Assert.Equal("PS C:\\repo>", GetVisiblePlainText(buffer).Trim());
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

    private static string GetVisiblePlainText(TerminalBuffer buffer)
    {
        var field = typeof(TerminalBuffer).GetField("_viewport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var viewport = (TerminalRow[])field!.GetValue(buffer)!;
        return string.Join("\n", viewport.Select(GetRowText)).TrimEnd();
    }

    private static string GetRowText(TerminalRow row)
    {
        var chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
        return new string(chars).TrimEnd();
    }
}
