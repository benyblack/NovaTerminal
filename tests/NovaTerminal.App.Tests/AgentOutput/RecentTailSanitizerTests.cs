using System;
using NovaTerminal.AgentOutput;
using Xunit;

namespace NovaTerminal.Tests.AgentOutput;

/// <summary>
/// Snapshot extraction: a recent-output tail of a terminal is rounds of prompt → response all
/// over the same grid, and the panel must render <b>the latest response</b> - prompt lines are
/// segment boundaries, echoed commands are dropped with them, and the last segment with content
/// wins.
/// </summary>
public sealed class RecentTailSanitizerTests
{
    /// <summary>A super-long agent chat, abridged: three rounds of prompt → response.</summary>
    private const string LongChat = """
        PS D:\projects> zcode -p "first question"
        ### First answer
        early content
        PS D:\projects> zcode -p "second question"
        ## Second answer
        middle content with more detail
        PS D:\projects> zcode -p "third question"
        ### Third answer
        latest content
        PS D:\projects>
        """;

    [Fact]
    public void SuperLongChat_YieldsOnlyTheLatestResponse()
    {
        string response = RecentTailSanitizer.ExtractLastResponse(LongChat);

        Assert.Equal("### Third answer\nlatest content", response);
    }

    [Fact]
    public void SingleCommand_YieldsItsResponse()
    {
        const string snapshot = "PS D:\\projects> aichat \"cheat sheet\"\n### Text styles\nplain body\nWant this saved?\nPS D:\\projects>";

        Assert.Equal("### Text styles\nplain body\nWant this saved?", RecentTailSanitizer.ExtractLastResponse(snapshot));
    }

    [Fact]
    public void ResponseWithBlankLines_KeepsThem()
    {
        const string snapshot = "PS D:\\p> run\nfirst para\n\nsecond para\nPS D:\\p>";

        Assert.Equal("first para\n\nsecond para", RecentTailSanitizer.ExtractLastResponse(snapshot));
    }

    [Fact]
    public void NoPromptsAtAll_IsReturnedUnchanged()
    {
        const string response = "## Answer\nwith detail";

        Assert.Equal(response, RecentTailSanitizer.ExtractLastResponse(response));
    }

    [Fact]
    public void PosixPrompt_SplitsToo()
    {
        const string snapshot = "user@host:~/demo$ llm \"hi\"\n**Hello!**\nuser@host:~/demo$ llm \"bye\"\n**Bye!**\nuser@host:~/demo$";

        Assert.Equal("**Bye!**", RecentTailSanitizer.ExtractLastResponse(snapshot));
    }

    [Fact]
    public void CmdPrompt_SplitsToo()
    {
        const string snapshot = "D:\\projects> agent.exe\n## Result\ndone\nD:\\projects> agent.exe --again\n## Result 2\nfine\nD:\\projects>";

        Assert.Equal("## Result 2\nfine", RecentTailSanitizer.ExtractLastResponse(snapshot));
    }

    [Fact]
    public void EmptySegments_AreSkipped()
    {
        // A trailing bare prompt is an empty segment; the response before it wins.
        const string snapshot = "PS D:\\p> first\ncontent\nPS D:\\p>";

        Assert.Equal("content", RecentTailSanitizer.ExtractLastResponse(snapshot));
    }

    [Fact]
    public void AllPromptsAndBlanks_TrimsToEmpty()
    {
        Assert.Equal(string.Empty, RecentTailSanitizer.ExtractLastResponse("PS D:\\p>\nPS D:\\p> dir\nPS D:\\p>"));
    }

    [Fact]
    public void Null_IsEmpty()
    {
        Assert.Equal(string.Empty, RecentTailSanitizer.ExtractLastResponse(null!));
    }
}
