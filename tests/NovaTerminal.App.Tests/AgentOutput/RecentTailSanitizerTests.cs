using System;
using NovaTerminal.AgentOutput;
using Xunit;

namespace NovaTerminal.Tests.AgentOutput;

/// <summary>
/// The snapshot sanitizer: prompt lines off both ends of a recent-tail snapshot, response text
/// untouched. Every case maps to what the panel's user saw in practice - a PowerShell agent run
/// whose tail was "PS D:\projects&gt;" and whose head was the echoed prompt+command.
/// </summary>
public sealed class RecentTailSanitizerTests
{
    [Fact]
    public void PowerShellPromptAndEcho_AreTrimmedFromBothEnds()
    {
        const string snapshot = "PS D:\\projects> aichat \"cheat sheet\"\n### Text styles\nplain body\nWant this saved?\nPS D:\\projects>";

        string trimmed = RecentTailSanitizer.Trim(snapshot);

        Assert.Equal("### Text styles\nplain body\nWant this saved?", trimmed);
    }

    [Fact]
    public void CmdPrompt_IsTrimmed()
    {
        const string snapshot = "D:\\projects> agent.exe\n## Result\ndone\nD:\\projects>";

        Assert.Equal("## Result\ndone", RecentTailSanitizer.Trim(snapshot));
    }

    [Fact]
    public void PosixPrompt_IsTrimmed()
    {
        const string snapshot = "user@host:~/demo$ llm \"hi\"\n**Hello!**\nuser@host:~/demo$";

        Assert.Equal("**Hello!**", RecentTailSanitizer.Trim(snapshot));
    }

    [Fact]
    public void BlankLinesAtTheEnds_GoWithThePrompts()
    {
        const string snapshot = "PS D:\\p> cmd\n\nbody line\n\n\nPS D:\\p>";

        Assert.Equal("body line", RecentTailSanitizer.Trim(snapshot));
    }

    [Fact]
    public void MidResponsePromptShapedLines_AreKept()
    {
        // Only the two ends are touched. A response that quotes a prompt mid-body keeps it -
        // trimming content the agent actually printed would be lying about the response.
        const string snapshot = "PS D:\\p> q\nanswer line\nimagine a prompt here > like this\nPS D:\\p>";

        string trimmed = RecentTailSanitizer.Trim(snapshot);

        Assert.Contains("imagine a prompt here > like this", trimmed, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponseLinesEndingInDollar_AreKept()
    {
        const string snapshot = "PS D:\\p> price check\ncosts 5$\ntotal 10 euros\nPS D:\\p>";

        string trimmed = RecentTailSanitizer.Trim(snapshot);

        Assert.Contains("costs 5$", trimmed, StringComparison.Ordinal);
    }

    [Fact]
    public void AllPromptLines_TrimsToEmpty()
    {
        Assert.Equal(string.Empty, RecentTailSanitizer.Trim("PS D:\\p>\nPS D:\\p> dir\nPS D:\\p>"));
    }

    [Fact]
    public void NoPromptsAtAll_IsReturnedUnchanged()
    {
        const string response = "## Answer\nwith detail";

        Assert.Equal(response, RecentTailSanitizer.Trim(response));
    }
}
