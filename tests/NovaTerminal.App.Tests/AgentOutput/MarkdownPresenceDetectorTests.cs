using System;
using NovaTerminal.AgentOutput;
using Xunit;

namespace NovaTerminal.Tests.AgentOutput;

/// <summary>
/// The markdown-presence heuristic that gates the MD toggle's visibility. The two error
/// directions have different costs - a false positive is a pointless button, a false negative is
/// a hidden feature - so these tests pin the boundary from both sides: real agent-style output
/// must clear the bar, and the noise terminal output is full of (log separators, comments,
/// dash fragments) must not.
/// </summary>
public sealed class MarkdownPresenceDetectorTests
{
    private const string AgentStyleOutput = """
        Analyzing the project...

        ## Plan
        1. Read the config
        2. Apply fixes

        | File | Status |
        |------|--------|
        | a.cs | fixed |

        ```csharp
        var x = 1;
        ```

        - [x] done
        - [ ] pending
        """;

    [Fact]
    public void AgentStyleOutput_IsDetected()
    {
        Assert.True(MarkdownPresenceDetector.LooksLikeMarkdown(AgentStyleOutput));
    }

    [Fact]
    public void HeadingPlusFencedCode_IsDetected()
    {
        const string markdown = "## Summary\nFixed the bug.\n\n```\npatch text\n```";
        Assert.True(MarkdownPresenceDetector.LooksLikeMarkdown(markdown));
    }

    [Fact]
    public void HeadingPlusTable_IsDetected()
    {
        const string markdown = "# Results\n\n| a | b |\n|---|---|\n| 1 | 2 |";
        Assert.True(MarkdownPresenceDetector.LooksLikeMarkdown(markdown));
    }

    [Fact]
    public void HeadingPlusTwoListItems_IsDetected()
    {
        const string markdown = "## Next steps\n- rebuild\n- redeploy";
        Assert.True(MarkdownPresenceDetector.LooksLikeMarkdown(markdown));
    }

    [Fact]
    public void NumberedListPlusHeading_IsDetected()
    {
        const string markdown = "## Steps\n1. first\n2. second";
        Assert.True(MarkdownPresenceDetector.LooksLikeMarkdown(markdown));
    }

    // ---------------------------------------------------------------- noise that must not trigger

    [Fact]
    public void PlainProgramOutput_IsNotDetected()
    {
        const string output = "Build started...\nCompiling 12 files\nBuild succeeded.\n    0 Warning(s)";
        Assert.False(MarkdownPresenceDetector.LooksLikeMarkdown(output));
    }

    [Fact]
    public void SingleHeadingAlone_IsNotDetected()
    {
        // One heading is a single strong signal; build logs and scripts legitimately emit them.
        const string output = "# build log header\ngcc -o foo foo.c\ndone";
        Assert.False(MarkdownPresenceDetector.LooksLikeMarkdown(output));
    }

    [Fact]
    public void HashWithoutSpace_IsNotAHeading()
    {
        // "#tag" style fragments are not ATX headings.
        const string output = "#tag1 released\n#tag2 released\nsee the changelog";
        Assert.False(MarkdownPresenceDetector.LooksLikeMarkdown(output));
    }

    [Fact]
    public void SeparatorLines_AreNotTables()
    {
        // Log/rules separators are dashes without pipes.
        const string output = "## 章节\n--------\nplain text\n--------\nmore text";
        Assert.False(MarkdownPresenceDetector.LooksLikeMarkdown(output));
    }

    [Fact]
    public void SingleDashBullet_IsBelowTheBar()
    {
        const string output = "notes:\n- first item only";
        Assert.False(MarkdownPresenceDetector.LooksLikeMarkdown(output));
    }

    [Fact]
    public void EmptyAndNull_AreNotDetected()
    {
        Assert.False(MarkdownPresenceDetector.LooksLikeMarkdown(string.Empty));
        Assert.False(MarkdownPresenceDetector.LooksLikeMarkdown(null!));
    }

    [Fact]
    public void BareFenceAlone_IsBelowTheBar()
    {
        // ``` on its own shows up in shell transcripts; one strong signal is not enough.
        const string output = "$ cat out.txt\n```\nsome text\n```";
        Assert.False(MarkdownPresenceDetector.LooksLikeMarkdown(output));
    }
}
