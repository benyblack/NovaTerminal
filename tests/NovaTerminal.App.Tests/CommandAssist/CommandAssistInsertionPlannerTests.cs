using System.Collections.Generic;
using NovaTerminal.CommandAssist.Application;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// The planner's contract in three parts: the suffix arithmetic (unchanged since V1), the refusals
/// (Phase 1c, and the reason the arithmetic can be trusted), and the replace style (history search
/// only, and the reason the refusals still hold under it).
/// </summary>
/// <remarks>
/// <para>
/// Every refusal here is a line V1 would have happily computed a delta against, using a keystroke
/// mirror that had no idea the line had moved. They are written as theories over both
/// <see cref="CommandAssistInsertionStyle"/> values wherever the rule is shared, because the thing
/// that must not drift is precisely that the two styles refuse the <em>same</em> unreadable lines -
/// replace relaxes one condition and one only.
/// </para>
/// </remarks>
public sealed class CommandAssistInsertionPlannerTests
{
    private const CommandAssistInsertionStyle Append = CommandAssistInsertionStyle.Append;
    private const CommandAssistInsertionStyle Replace = CommandAssistInsertionStyle.ReplaceTypedPrefix;

    private static AssistQuerySnapshot Line(
        string text,
        int? cursorOffset = null,
        bool isMultiline = false,
        bool rightPromptTrimmed = false)
    {
        return new AssistQuerySnapshot(text, cursorOffset ?? text.Length, isMultiline, rightPromptTrimmed);
    }

    /// <summary>
    /// A line the reader classified as "typed prefix, then a ghost": the whole painted line, the cursor
    /// at the end of the typed part, and the flag set.
    /// </summary>
    private static AssistQuerySnapshot Ghosted(string typed, string ghost) =>
        new(typed + ghost, typed.Length, IsMultiline: false, RightPromptTrimmed: false, TextAfterCursorIsGhost: true);

    /// <summary>Asserts a refusal and that nothing was written to the out parameter.</summary>
    private static void AssertRefuses(
        AssistQuerySnapshot? query,
        string? selectedCommand,
        CommandAssistInsertionStyle style)
    {
        Assert.False(CommandAssistInsertionPlanner.TryCreatePlan(
            query,
            selectedCommand,
            style,
            out CommandAssistInsertionPlan plan));

        Assert.Equal(default, plan);
    }

    /// <summary>Asserts a successful plan and returns it.</summary>
    private static CommandAssistInsertionPlan AssertPlans(
        AssistQuerySnapshot? query,
        string? selectedCommand,
        CommandAssistInsertionStyle style)
    {
        Assert.True(CommandAssistInsertionPlanner.TryCreatePlan(
            query,
            selectedCommand,
            style,
            out CommandAssistInsertionPlan plan));

        return plan;
    }

    // ---------------------------------------- the typed prefix projection (PR #293, blocker 1)

    /// <summary>
    /// <c>TextBeforeCursor</c> is the query projection every prefix consumer wants: the painted line up
    /// to the cursor, so PSReadLine's inline prediction - real cells past the cursor - is left out.
    /// </summary>
    [Theory]
    [InlineData("echo hello-world", 2, "ec")]
    [InlineData("echo hello-world", 1, "e")]
    [InlineData("git status", 10, "git status")]
    [InlineData("git status", 4, "git ")]
    [InlineData("", 0, "")]
    public void TextBeforeCursor_IsTheLineUpToTheCursor(string text, int cursorOffset, string expected)
    {
        Assert.Equal(expected, Line(text, cursorOffset).TextBeforeCursor);
    }

    /// <summary>
    /// Clamped rather than trusting the invariant. <c>CursorOffset</c> is documented as always valid, and
    /// two comparisons remove a whole class of crash from a future reader change.
    /// </summary>
    [Theory]
    [InlineData("git status", -1, "")]
    [InlineData("git status", 99, "git status")]
    public void TextBeforeCursor_ClampsAnOutOfRangeCursor(string text, int cursorOffset, string expected)
    {
        Assert.Equal(expected, Line(text, cursorOffset).TextBeforeCursor);
    }

    /// <summary>
    /// And it is not a substitute for <c>IsUsableAsTypedPrefix</c>: a prediction and a mid-line cursor are
    /// indistinguishable on the grid, so insertion still refuses on both. Having a good prefix to rank on
    /// does not make an append safe - nor a replace, whose deletes would eat the head of the line and
    /// leave the tail sitting after the inserted command.
    /// </summary>
    [Theory]
    [InlineData(Append)]
    [InlineData(Replace)]
    public void TextBeforeCursor_DoesNotMakeAMidLineCursorInsertable(CommandAssistInsertionStyle style)
    {
        AssistQuerySnapshot line = Line("echo hello-world", cursorOffset: 2);

        Assert.Equal("ec", line.TextBeforeCursor);
        Assert.False(line.IsUsableAsTypedPrefix);
        AssertRefuses(line, "echo hello", style);

        Assert.False(CommandAssistInsertionPlanner.TryCreateInsertion(line, "echo hello", out string? textToSend));
        Assert.Null(textToSend);
    }

    // ---------------------------------------------------------------- the additive arithmetic

    [Fact]
    public void TryCreateInsertion_WhenLineIsPrefix_ReturnsOnlySuffix()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git st"),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("atus", textToSend);
    }

    [Fact]
    public void TryCreateInsertion_WhenLineMatchesExactly_ReturnsFalse()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git status"),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }

    /// <summary>
    /// An empty line is a fact the grid reported, not a missing one, so the whole command is safe
    /// to send. The distinction between this and the markless case below is the entire point of
    /// modelling the query as a nullable snapshot rather than a string.
    /// </summary>
    [Fact]
    public void TryCreateInsertion_WhenTheLineIsObservedEmpty_ReturnsTheFullSuggestion()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line(string.Empty),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("git status", textToSend);
    }

    [Fact]
    public void TryCreateInsertion_WhenSuggestionDoesNotStartWithTheLine_ReturnsFalse()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("kubectl"),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.False(created);
        Assert.Null(textToSend);
    }

    [Theory]
    [InlineData(Append)]
    [InlineData(Replace)]
    public void TryCreateInsertion_WhenSelectedCommandIsEmpty_ReturnsFalse(CommandAssistInsertionStyle style)
    {
        AssertRefuses(Line("git st"), string.Empty, style);

        Assert.False(CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git st"),
            selectedCommand: string.Empty,
            out string? textToSend));
        Assert.Null(textToSend);
    }

    /// <summary>
    /// Refusal 1 - no grid truth. A markless session cannot see the command line, so it cannot know
    /// that sending "git status" onto a line already reading "git s" produces "git sgit status".
    /// Replace needs strictly more than that - a <em>count</em> - so it refuses here too, and this is
    /// the whole of the degraded-mode answer: no new gate anywhere, because the one markless case where
    /// the count is knowable is the case where it is zero.
    /// </summary>
    [Theory]
    [InlineData(Append)]
    [InlineData(Replace)]
    public void TryCreateInsertion_WithoutGridTruth_Refuses(CommandAssistInsertionStyle style)
    {
        AssertRefuses(query: null, "git status", style);

        Assert.False(CommandAssistInsertionPlanner.TryCreateInsertion(
            query: null,
            selectedCommand: "git status",
            out string? textToSend));
        Assert.Null(textToSend);
    }

    /// <summary>
    /// Refusal 2 - the cursor is not at the end. The user pressed Home or Left; sent text lands at
    /// the cursor, so the "suffix" would be spliced into the middle of the command - and a replace's
    /// deletes run leftwards from there, erasing the head and leaving the tail behind.
    /// </summary>
    [Theory]
    [InlineData(0, Append)]
    [InlineData(3, Append)]
    [InlineData(5, Append)]
    [InlineData(0, Replace)]
    [InlineData(3, Replace)]
    [InlineData(5, Replace)]
    public void TryCreateInsertion_WhenTheCursorIsNotAtTheEnd_Refuses(
        int cursorOffset,
        CommandAssistInsertionStyle style)
    {
        AssertRefuses(Line("git st", cursorOffset), "git status", style);
    }

    /// <summary>
    /// Refusal 3 - multiline. The snapshot contains whatever the shell painted as a continuation
    /// prompt, so the text is not a prefix the user typed even when it happens to start one - and it is
    /// not a count of anything the user typed either, which is why replace cannot relax it.
    /// </summary>
    [Theory]
    [InlineData(Append)]
    [InlineData(Replace)]
    public void TryCreateInsertion_WhenTheEntryIsMultiline_Refuses(CommandAssistInsertionStyle style)
    {
        AssertRefuses(
            Line("for i in 1 2 3\n> do echo", isMultiline: true),
            "for i in 1 2 3\n> do echo $i; done",
            style);
    }

    /// <summary>
    /// <strong>A trimmed right prompt is no longer a refusal, and this is the owner's "Enter puts
    /// nothing in the terminal on Windows PowerShell" report at planner scope.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to be refusal 4, on the reasoning that the tail of the line was the reader's inference
    /// and the tail is what a suffix append attaches to. The first half is true and the second is not:
    /// <c>GridQueryReader.FindRightPromptGapStart</c> floors its search at the cursor column, so the
    /// trim boundary is always at or after the cursor and never removes anything the append is measured
    /// against. What survives is <see cref="AssistQuerySnapshot.IsUsableAsTypedPrefix"/>'s cursor test,
    /// which is the real guard. The same floor is why replace never reaches into the trimmed region
    /// either: it deletes leftwards from a cursor that is at or before the trim.
    /// </para>
    /// <para>
    /// Left in as a refusal it made Command Assist inert for anyone with a right-aligned prompt -
    /// oh-my-posh, zsh <c>RPROMPT</c>, starship - because the flag is then set on every prompt the
    /// shell paints.
    /// </para>
    /// </remarks>
    [Fact]
    public void TryCreateInsertion_WhenARightPromptWasTrimmed_StillSendsTheSuffix()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line("git st", rightPromptTrimmed: true),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("atus", textToSend);
    }

    /// <summary>
    /// The empty-line case of the same prompt, which is the one the owner actually pressed
    /// <c>Enter</c> on: <c>Ctrl+R</c> at a bare prompt whose row carries a right-aligned badge.
    /// </summary>
    [Fact]
    public void TryCreateInsertion_OnAnEmptyLineWithARightPromptTrimmed_SendsTheWholeCommand()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line(string.Empty, rightPromptTrimmed: true),
            selectedCommand: "git status",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("git status", textToSend);
    }

    /// <summary>
    /// The guard that did the work all along is untouched, for both styles: a trimmed right prompt with
    /// the cursor somewhere else on the line still refuses, because the cursor is not at the end.
    /// </summary>
    [Theory]
    [InlineData(Append)]
    [InlineData(Replace)]
    public void TryCreateInsertion_WhenARightPromptWasTrimmedAndTheCursorIsMidLine_StillRefuses(
        CommandAssistInsertionStyle style)
    {
        AssertRefuses(
            Line("git st", cursorOffset: 3, rightPromptTrimmed: true),
            "git status",
            style);
    }

    /// <summary>
    /// The V1 desync cases, stated as the planner sees them. Each is a line the shell rewrote
    /// without emitting anything a keystroke mirror could observe; each now produces the delta the
    /// grid implies rather than the delta a stale mirror implied.
    /// </summary>
    [Theory]
    // Ctrl+U: the mirror still held "git st"; V1 would have sent "atus" onto an empty line.
    [InlineData("", "git status", "git status")]
    // Up-arrow history recall: the line is now a full command the user never typed a character of.
    [InlineData("git status", "git status --short", " --short")]
    // Shell-side Tab completion: the shell finished the word for the user.
    [InlineData("git status", "git status --porcelain", " --porcelain")]
    public void TryCreateInsertion_AfterTheShellRewroteTheLine_FollowsTheGrid(
        string lineAfterShellEdit,
        string selectedCommand,
        string expected)
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line(lineAfterShellEdit),
            selectedCommand,
            out string? textToSend);

        Assert.True(created);
        Assert.Equal(expected, textToSend);
    }

    // ------------------------------------------- inline predictions (dogfood round 4, item 1)

    /// <summary>
    /// The bug the owner hit, at the layer it is fixed. With a prediction painted past the cursor the
    /// line reads as usable again, because a prediction is not text the user typed - it lives in the
    /// shell's suggestion buffer, never in its input buffer, and the next keystroke recomputes it.
    /// </summary>
    [Fact]
    public void GhostSuffix_MakesTheLineUsableAsATypedPrefix()
    {
        AssistQuerySnapshot line = Ghosted("docke", "r ps -a");

        Assert.True(line.IsUsableAsTypedPrefix);
        Assert.Equal("docke", line.TypedPrefix);
    }

    /// <summary>
    /// And the delta is computed against the typed characters, not against the whole painted line. This
    /// is the assertion that catches the half-fix: flip <c>TypedPrefix</c> back to <c>Text</c> in the
    /// planner and the suffix comes out empty or wrong, because the prediction the shell guessed is not
    /// the suggestion the user picked.
    /// </summary>
    [Fact]
    public void GhostSuffix_IsMeasuredAgainstTheTypedCharactersOnly()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Ghosted("docke", "r ps -a"),
            selectedCommand: "docker compose up",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("r compose up", textToSend);
    }

    /// <summary>
    /// One typed character and a full-line prediction - the shape a fresh prompt produces on the first
    /// keystroke, and the one that used to refuse hardest.
    /// </summary>
    [Fact]
    public void GhostSuffix_WorksFromASingleTypedCharacter()
    {
        bool created = CommandAssistInsertionPlanner.TryCreateInsertion(
            Ghosted("d", "ocker compose up"),
            selectedCommand: "docker ps",
            out string? textToSend);

        Assert.True(created);
        Assert.Equal("ocker ps", textToSend);
    }

    /// <summary>
    /// The typed part still has to be a prefix of the suggestion <em>under append</em>. The ghost widens
    /// which lines are readable; it does not weaken what makes an append correct.
    /// </summary>
    [Fact]
    public void GhostSuffix_StillRefusesWhenTheTypedTextIsNotAPrefix()
    {
        Assert.False(CommandAssistInsertionPlanner.TryCreateInsertion(
            Ghosted("kubect", "l get pods"),
            selectedCommand: "docker ps",
            out string? textToSend));

        Assert.Null(textToSend);
    }

    /// <summary>
    /// A multiline entry is refused whatever the flag says. The two facts are independent - the flag
    /// describes the region past the cursor, and a continuation prompt is text before it that the user
    /// never typed - so the multiline term is kept as its own conjunct rather than folded in.
    /// </summary>
    [Fact]
    public void GhostSuffix_DoesNotOverrideTheMultilineRefusal()
    {
        var line = new AssistQuerySnapshot(
            "git commit\n> ",
            CursorOffset: 10,
            IsMultiline: true,
            RightPromptTrimmed: false,
            TextAfterCursorIsGhost: true);

        Assert.False(line.IsUsableAsTypedPrefix);
        AssertRefuses(line, "git commit --amend", Append);
        AssertRefuses(line, "git commit --amend", Replace);
    }

    /// <summary>
    /// Without the flag nothing changes: a mid-line cursor refuses exactly as it did before this round.
    /// The flag is the reader's proof, and no proof means no licence - for either style.
    /// </summary>
    [Theory]
    [InlineData(Append)]
    [InlineData(Replace)]
    public void WithoutTheGhostFlag_AMidLineCursorStillRefuses(CommandAssistInsertionStyle style)
    {
        AssistQuerySnapshot line = Line("docker ps -a", cursorOffset: 5);

        Assert.False(line.IsUsableAsTypedPrefix);
        Assert.Equal("docker ps -a", line.TypedPrefix);
        AssertRefuses(line, "docker compose up", style);
    }

    // ------------------------------------------------ the replace style (explicit history search)

    /// <summary>
    /// <strong>The reported bug, verbatim.</strong> Type <c>git</c>, open <c>Ctrl+R</c>, pick
    /// <c>echo git-alpha</c> - a row the subsequence filter matched and no prefix rule ever will - and
    /// press <c>Enter</c>. Under append the planner refused, <c>Enter</c> fell through to the shell, and
    /// the shell ran <c>git</c>: the user asked for one command and got another.
    /// </summary>
    [Fact]
    public void UnderReplace_ANonPrefixRowErasesTheQueryAndSendsTheWholeCommand()
    {
        CommandAssistInsertionPlan plan = AssertPlans(Line("git"), "echo git-alpha", Replace);

        Assert.Equal(3, plan.BackspaceCount);
        Assert.Equal("echo git-alpha", plan.TextToSend);
    }

    /// <summary>The plan sibling of the append-only refusal above, at the same fixture.</summary>
    [Fact]
    public void UnderReplace_ARowThatDoesNotStartWithTheLineIsAccepted()
    {
        CommandAssistInsertionPlan plan = AssertPlans(Line("kubectl"), "git status", Replace);

        Assert.Equal(7, plan.BackspaceCount);
        Assert.Equal("git status", plan.TextToSend);
    }

    /// <summary>
    /// <strong>A prefix row under replace is still a full replace, not an append.</strong> Type
    /// <c>git</c>, take <c>git status</c>: three deletes and the whole command, never <c>(0, " status")</c>.
    /// </summary>
    /// <remarks>
    /// The optimisation is deliberately not taken. One behaviour per surface, so the user does not have
    /// to know which; the bytes on the wire must not depend on which row happens to be highlighted; and
    /// optimising here would make the delete path dead code on the <em>common</em> <c>Ctrl+R</c> accept
    /// and live only on the rarer non-prefix rows, which is how a path rots.
    /// </remarks>
    [Fact]
    public void UnderReplace_APrefixRowIsStillAFullReplaceRatherThanAnAppend()
    {
        CommandAssistInsertionPlan plan = AssertPlans(Line("git"), "git status", Replace);

        Assert.Equal(3, plan.BackspaceCount);
        Assert.Equal("git status", plan.TextToSend);
    }

    /// <summary>
    /// An empty line under replace is zero deletes and the whole command - numerically identical to the
    /// append answer, and produced by the same arithmetic rather than by a second branch. The exact
    /// zero is the assertion: a stray backspace here would ring the bell on every <c>Ctrl+R</c> accept
    /// at a bare prompt, which is the most common accept there is.
    /// </summary>
    [Fact]
    public void UnderReplace_AnEmptyLinePlansNoDeletesAtAll()
    {
        CommandAssistInsertionPlan plan = AssertPlans(Line(string.Empty), "git status", Replace);

        Assert.Equal(0, plan.BackspaceCount);
        Assert.Equal("git status", plan.TextToSend);
    }

    /// <summary>
    /// Exact match still refuses under replace. Erasing N characters to retype the identical N is pure
    /// flicker, and it is exactly the window in which "the deletes land but the insert does not" costs
    /// the user their line for nothing. It also keeps the invariant that a successful plan changes
    /// something.
    /// </summary>
    [Fact]
    public void UnderReplace_ALineThatAlreadyIsTheCommandRefuses()
    {
        AssertRefuses(Line("git status"), "git status", Replace);
    }

    /// <summary>
    /// The ghost is not backspaced. <c>kubect</c> typed with <c>l get pods</c> predicted past the cursor
    /// is <em>six</em> deletes, not sixteen: the prediction lives in the shell's suggestion buffer and
    /// there is nothing there to delete. Counting from <c>Text</c> instead of <c>TypedPrefix</c> is the
    /// mutation this catches.
    /// </summary>
    [Fact]
    public void UnderReplace_TheGhostSuffixIsNotCounted()
    {
        CommandAssistInsertionPlan plan = AssertPlans(Ghosted("kubect", "l get pods"), "docker ps", Replace);

        Assert.Equal(6, plan.BackspaceCount);
        Assert.Equal("docker ps", plan.TextToSend);
    }

    /// <summary>
    /// <strong>The counting unit: UTF-16 code units, not graphemes and not grid cells.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// These three cases exist so that a refactor towards grapheme counting has to argue with a test
    /// rather than with a comment.
    /// </para>
    /// <list type="bullet">
    /// <item><c>caf</c> + <c>e</c> + <c>U+0301</c> is four graphemes and five code units. readline needs
    /// five backward-deletes to clear it, because it deletes a codepoint at a time and the combining
    /// acute is its own codepoint. A grapheme count would say four and leave the accent on the line, and
    /// the inserted command would be appended to it - undershoot is the unrecoverable direction.</item>
    /// <item>Two CJK characters are two code units even though they occupy four grid columns. The count
    /// is a count of characters the editor holds, not of cells the terminal paints; the reader already
    /// collapses a wide character's trailing cell.</item>
    /// <item>One non-BMP emoji is a surrogate pair: two code units, one codepoint. Here the count
    /// <em>overshoots</em> - PSReadLine coalesces the pair and needs one delete - and that is the safe
    /// direction, because the extra delete lands at the start of an empty input buffer and does
    /// nothing (see <c>PtyBackspaceAtLineStartTests</c>).</item>
    /// </list>
    /// <para>
    /// The inputs are spelled with <c>\u</c> escapes rather than as literals on purpose: an editor, a
    /// source normalisation pass or an encoding round-trip could quietly turn the decomposed
    /// <c>e</c>-plus-accent into the precomposed character, which would leave the first case asserting
    /// four code units and testing nothing at all.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("cafe\u0301", 5)]
    [InlineData("\u4F60\u597D", 2)]
    [InlineData("\U0001F600", 2)]
    public void UnderReplace_TheBackspaceCountIsInUtf16CodeUnits(string typed, int expected)
    {
        CommandAssistInsertionPlan plan = AssertPlans(Line(typed), "git status", Replace);

        Assert.Equal(expected, plan.BackspaceCount);
        Assert.Equal(typed.Length, plan.BackspaceCount);
        Assert.Equal("git status", plan.TextToSend);
    }

    /// <summary>
    /// A row carrying a line break is refused by both styles. <c>SendInput</c> writes raw bytes with no
    /// bracketed-paste wrapping on this path, so the newline would <em>submit</em> the line rather than
    /// be inserted into it - and under replace it would submit against a line we had just erased.
    /// </summary>
    [Theory]
    [InlineData(Append)]
    [InlineData(Replace)]
    public void ARowContainingALineBreakIsRefused(CommandAssistInsertionStyle style)
    {
        AssertRefuses(Line(string.Empty), "echo one\necho two", style);
        AssertRefuses(Line(string.Empty), "echo one\r\necho two", style);
    }

    /// <summary>
    /// <strong>The invariant, as a property over the whole matrix: a successful plan is never
    /// <c>(0, "")</c>.</strong>
    /// </summary>
    /// <remarks>
    /// The pane re-checks this before sending, and this is why it can. A plan that changed nothing would
    /// dismiss the surface and send no bytes, which is indistinguishable to the user from the feature
    /// being broken - the failure PR #294 was about. Written as a sweep rather than as one more example
    /// so that a new refusal, or a new style, is covered the day it is added.
    /// </remarks>
    [Fact]
    public void ASuccessfulPlanNeverChangesNothing()
    {
        AssistQuerySnapshot?[] queries =
        {
            null,
            Line(string.Empty),
            Line("git"),
            Line("git status"),
            Line("kubectl"),
            Line("git st", cursorOffset: 2),
            Line("git st", rightPromptTrimmed: true),
            Line("for i in 1 2 3\n> do echo", isMultiline: true),
            Ghosted("kubect", "l get pods"),
            Ghosted("git", " status --short")
        };

        string?[] commands = { null, string.Empty, "git", "git status", "echo git-alpha", "echo a\nb" };
        CommandAssistInsertionStyle[] styles = { Append, Replace };

        var planned = new List<CommandAssistInsertionPlan>();

        foreach (AssistQuerySnapshot? query in queries)
        {
            foreach (string? command in commands)
            {
                foreach (CommandAssistInsertionStyle style in styles)
                {
                    if (!CommandAssistInsertionPlanner.TryCreatePlan(
                            query,
                            command,
                            style,
                            out CommandAssistInsertionPlan plan))
                    {
                        Assert.Equal(default, plan);
                        continue;
                    }

                    planned.Add(plan);
                    Assert.False(
                        plan.BackspaceCount == 0 && plan.TextToSend.Length == 0,
                        $"'{query?.Text ?? "<null>"}' + '{command}' ({style}) planned a no-op.");
                    Assert.True(plan.BackspaceCount >= 0);
                    Assert.NotNull(plan.TextToSend);
                }
            }
        }

        // The sweep has to actually reach the success path, or the property above is vacuous.
        Assert.NotEmpty(planned);
    }

    /// <summary>
    /// The append forwarder is exactly <c>TryCreatePlan</c> with the append style and no deletes - which
    /// is what lets every pre-existing additive test go on pinning the additive rule literally.
    /// </summary>
    [Theory]
    [InlineData("git st", "git status")]
    [InlineData("", "git status")]
    [InlineData("kubectl", "git status")]
    [InlineData("git status", "git status")]
    public void TryCreateInsertion_IsTryCreatePlanWithTheAppendStyle(string text, string selectedCommand)
    {
        bool viaForwarder = CommandAssistInsertionPlanner.TryCreateInsertion(
            Line(text),
            selectedCommand,
            out string? textToSend);

        bool viaPlan = CommandAssistInsertionPlanner.TryCreatePlan(
            Line(text),
            selectedCommand,
            Append,
            out CommandAssistInsertionPlan plan);

        Assert.Equal(viaPlan, viaForwarder);
        Assert.Equal(0, plan.BackspaceCount);
        Assert.Equal(viaPlan ? plan.TextToSend : null, textToSend);
    }
}
