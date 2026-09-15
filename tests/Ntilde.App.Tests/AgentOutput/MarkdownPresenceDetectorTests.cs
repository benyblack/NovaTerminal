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

    [Fact]
    public void BulletsOnlyAnswer_IsDetected()
    {
        // Reported from the running app: "claude -p explain X in 3 bullets" left the MD button
        // hidden. A bullets-only answer carries no heading, fence, table or task list, so the
        // strong-signal floor was unreachable at any number of bullets - and this is the single
        // most common shape the panel exists to render.
        const string output = """
            Here is the breakdown:

            - **AI Harness** wraps the model in a tool-calling loop.
            - **Context** is assembled per turn from files and prior messages.
            - **Tools** are declared as schemas the model can invoke.
            """;
        Assert.True(MarkdownPresenceDetector.LooksLikeMarkdown(output));
    }

    [Fact]
    public void NumberedAnswerWithoutAHeading_IsDetected()
    {
        // The same shape, ordered - agents alternate between the two freely.
        const string output = """
            Three steps:
            1. Read the config
            2. Apply fixes
            3. Re-run the build
            """;
        Assert.True(MarkdownPresenceDetector.LooksLikeMarkdown(output));
    }

    // ---------------------------------------------------------------- noise that must not trigger

    [Fact]
    public void TwoBulletsAlone_IsBelowTheBar()
    {
        // The floor is three: a bare pair of dash lines is common in build and install logs and
        // carries no other markdown structure to corroborate it.
        const string output = """
            notes:
            - first
            - second
            """;
        Assert.False(MarkdownPresenceDetector.LooksLikeMarkdown(output));
    }

    [Fact]
    public void ThreeBulletShellBanner_IsDetected_AcceptedFalsePositive()
    {
        // The documented cost of the three-list-line floor: clink's startup banner is three dash
        // bullets, so a fresh cmd pane shows the MD button. Accepted on this class's stated bias -
        // a pointless button is cheap, a hidden feature is not - and it ages out of the panel's
        // recent-tail window as soon as real output scrolls in.
        const string output = """
            Clink v1.9.32 is available.
            - To apply the update, run 'clink update'.
            - To stop checking for updates, run 'clink set clink.autoupdate off'.
            - To view the release notes, visit the Releases page:
            """;
        Assert.True(MarkdownPresenceDetector.LooksLikeMarkdown(output));
    }

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
