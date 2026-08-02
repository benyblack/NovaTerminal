using NovaTerminal.CommandAssist.Application;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistAnchorCalculatorTests
{
    [Fact]
    public void Calculate_WhenSpaceExistsAbovePrompt_PlacesBubbleAbovePrompt()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 1000,
            PaneHeight: 700,
            CellHeight: 18,
            CursorVisualRow: 34,
            VisibleRows: 36,
            BubbleWidth: 420,
            BubbleHeight: 36,
            PopupWidth: 520,
            PopupHeight: 220));

        Assert.True(layout.BubbleRect.Bottom < layout.PromptRect.Top);
        Assert.Equal(4, layout.PromptRect.Top - layout.BubbleRect.Bottom, precision: 1);
        Assert.Equal(CommandAssistPopupDirection.Upward, layout.PopupDirection);
        Assert.True(layout.PopupRect.Bottom <= layout.BubbleRect.Top);
    }

    [Fact]
    public void Calculate_WhenInsufficientRoomAbovePrompt_FlipsPopupBelowBubble()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 240,
            CellHeight: 18,
            CursorVisualRow: 2,
            VisibleRows: 12,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180));

        Assert.Equal(CommandAssistPopupDirection.Downward, layout.PopupDirection);
        Assert.True(layout.PopupRect.Top >= layout.BubbleRect.Bottom);
    }

    [Fact]
    public void Calculate_WhenPromptIsOnTopVisibleRow_PlacesBubbleBelowPrompt()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 0,
            VisibleRows: 24,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180));

        Assert.True(layout.BubbleRect.Top >= layout.PromptRect.Bottom,
            $"Expected bubble top {layout.BubbleRect.Top} to be below prompt bottom {layout.PromptRect.Bottom}.");
        Assert.Equal(layout.PromptRect.Height + 4, layout.BubbleRect.Top - layout.PromptRect.Bottom, precision: 1);
        Assert.True(layout.BubbleRect.Bottom <= layout.PopupRect.Top,
            $"Expected upward popup to clear bubble bottom {layout.BubbleRect.Bottom}, but popup top was {layout.PopupRect.Top}.");
    }

    [Fact]
    public void Calculate_WhenPromptIsInUpperStartupBand_PlacesBubbleBelowPrompt()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 2,
            VisibleRows: 24,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180));

        Assert.True(layout.BubbleRect.Top >= layout.PromptRect.Bottom,
            $"Expected startup-band bubble top {layout.BubbleRect.Top} to be below prompt bottom {layout.PromptRect.Bottom}.");
        Assert.Equal(layout.PromptRect.Height + 4, layout.BubbleRect.Top - layout.PromptRect.Bottom, precision: 1);
    }

    [Fact]
    public void Calculate_WhenPromptIsInUpperStartupBand_LeavesInputRowClearanceBelowPrompt()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 1,
            VisibleRows: 24,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180));

        double expectedMinimumTop = layout.PromptRect.Bottom + layout.PromptRect.Height + 4;
        Assert.True(layout.BubbleRect.Top >= expectedMinimumTop,
            $"Expected startup-band bubble top {layout.BubbleRect.Top} to clear one input row below prompt bottom {layout.PromptRect.Bottom}, but expected at least {expectedMinimumTop}.");
    }

    [Fact]
    public void Calculate_WhenBubbleIsBelowPromptAndBothVerticalDirectionsFit_PrefersDownwardPopup()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 900,
            PaneHeight: 700,
            CellHeight: 18,
            CursorVisualRow: 14,
            VisibleRows: 30,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 220));

        Assert.True(layout.BubbleRect.Top >= layout.PromptRect.Bottom,
            $"Expected bubble to sit below prompt, but bubble top {layout.BubbleRect.Top} was above prompt bottom {layout.PromptRect.Bottom}.");
        Assert.Equal(CommandAssistPopupDirection.Downward, layout.PopupDirection);
        Assert.True(layout.PopupRect.Top >= layout.BubbleRect.Bottom,
            $"Expected downward popup top {layout.PopupRect.Top} to be below bubble bottom {layout.BubbleRect.Bottom}.");
    }

    [Fact]
    public void Calculate_WhenPromptAnchorIsReliable_AlignsPromptTopToCursorRowWithoutExtraTopPadding()
    {
        var calculator = new CommandAssistAnchorCalculator();

        const int cursorRow = 2;
        const double cellHeight = 18;
        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 540,
            CellHeight: cellHeight,
            CursorVisualRow: cursorRow,
            VisibleRows: 24,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasReliablePromptAnchor: true));

        double expectedPromptTop = cursorRow * cellHeight;
        Assert.Equal(expectedPromptTop, layout.PromptRect.Top, precision: 1);
    }

    [Fact]
    public void Calculate_WhenPromptAnchorIsUnreliableAndCursorBandFallbackIsUsed_AlignsPromptTopToCursorRowWithoutExtraTopPadding()
    {
        var calculator = new CommandAssistAnchorCalculator();

        const int cursorRow = 18;
        const double cellHeight = 18;
        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 900,
            PaneHeight: 540,
            CellHeight: cellHeight,
            CursorVisualRow: cursorRow,
            VisibleRows: 24,
            BubbleWidth: 380,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasReliablePromptAnchor: false));

        double expectedPromptTop = cursorRow * cellHeight;
        Assert.Equal(expectedPromptTop, layout.PromptRect.Top, precision: 1);
    }

    [Fact]
    public void Calculate_WhenPromptAnchorIsReliableAndPaneIsShort_PlacesBubbleBelowPromptConservatively()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 900,
            PaneHeight: 220,
            CellHeight: 18,
            CursorVisualRow: 5,
            VisibleRows: 9,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180));

        Assert.True(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Top >= layout.PromptRect.Bottom,
            $"Expected reliable short-pane bubble top {layout.BubbleRect.Top} to be below prompt bottom {layout.PromptRect.Bottom}.");
        Assert.Equal(layout.PromptRect.Height + 4, layout.BubbleRect.Top - layout.PromptRect.Bottom, precision: 1);
    }

    [Fact]
    public void Calculate_WhenPaneIsShortButWide_UsesSideFloatingPopup()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 220,
            CellHeight: 18,
            CursorVisualRow: 8,
            VisibleRows: 12,
            BubbleWidth: 320,
            BubbleHeight: 36,
            PopupWidth: 360,
            PopupHeight: 180));

        Assert.Equal(CommandAssistPopupDirection.RightSide, layout.PopupDirection);
        Assert.True(layout.PopupRect.Left >= layout.BubbleRect.Right);
    }

    [Fact]
    public void Calculate_WhenRectsWouldOverflow_ClampsInsidePaneBounds()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 320,
            PaneHeight: 160,
            CellHeight: 18,
            CursorVisualRow: 8,
            VisibleRows: 9,
            BubbleWidth: 420,
            BubbleHeight: 36,
            PopupWidth: 520,
            PopupHeight: 220));

        AssertRectWithin(layout.BubbleRect, 320, 160);
        AssertRectWithin(layout.PopupRect, 320, 160);
        AssertRectWithin(layout.PromptRect, 320, 160);
        Assert.True(layout.UseCompactBubbleLayout);
        Assert.True(layout.PopupRect.Height < 220);
    }

    [Fact]
    public void Calculate_WhenPromptAnchorIsUnreliable_UsesStableLowerSafeZoneFallback()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 900,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 0,
            VisibleRows: 0,
            BubbleWidth: 380,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasReliablePromptAnchor: false));

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Bottom > 540 * 0.5);
        Assert.True(layout.BubbleRect.Right <= 900);
        Assert.True(layout.PopupRect.Bottom <= layout.BubbleRect.Top);
    }

    [Fact]
    public void Calculate_WhenPromptAnchorIsUnreliableButCursorIsSettledLow_PlacesBubbleNearCursorBand()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 900,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 18,
            VisibleRows: 24,
            BubbleWidth: 380,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasReliablePromptAnchor: false));

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Bottom < 540 * 0.8,
            $"Expected settled SSH fallback bubble to sit near the cursor band instead of the lower safe zone, but bottom was {layout.BubbleRect.Bottom}.");
        Assert.True(layout.BubbleRect.Bottom <= layout.PromptRect.Top,
            $"Expected settled fallback bubble bottom {layout.BubbleRect.Bottom} to clear heuristic prompt top {layout.PromptRect.Top}.");
    }

    [Fact]
    public void Calculate_WhenPromptAnchorIsUnreliableAndVisibleRowsAreTiny_UsesLowerSafeZoneFallback()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 900,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 2,
            VisibleRows: 4,
            BubbleWidth: 380,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasReliablePromptAnchor: false));

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Bottom > 540 * 0.5,
            $"Expected tiny-row startup fallback bubble to stay in the lower safe zone, but bottom was {layout.BubbleRect.Bottom}.");
    }

    [Fact]
    public void Calculate_WhenPromptAnchorIsUnreliableAndCursorBandFallsInUpperArea_PlacesBubbleBelowPrompt()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 900,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 4,
            VisibleRows: 8,
            BubbleWidth: 380,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasReliablePromptAnchor: false));

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.PromptRect.Top <= 540 * 0.45,
            $"Expected fallback prompt to be in the upper area, but top was {layout.PromptRect.Top}.");
        Assert.True(layout.BubbleRect.Top >= layout.PromptRect.Bottom,
            $"Expected fallback bubble top {layout.BubbleRect.Top} to sit below prompt bottom {layout.PromptRect.Bottom}.");
        Assert.Equal(4, layout.BubbleRect.Top - layout.PromptRect.Bottom, precision: 1);
    }

    [Fact]
    public void Calculate_WhenFallbackPromptIsNearMidOnShortPane_PlacesBubbleBelowPromptConservatively()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 900,
            PaneHeight: 220,
            CellHeight: 18,
            CursorVisualRow: 5,
            VisibleRows: 9,
            BubbleWidth: 380,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasReliablePromptAnchor: false));

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Top >= layout.PromptRect.Bottom,
            $"Expected conservative fallback bubble top {layout.BubbleRect.Top} to be below prompt bottom {layout.PromptRect.Bottom} on short panes.");
        Assert.Equal(4, layout.BubbleRect.Top - layout.PromptRect.Bottom, precision: 1);
    }

    [Fact]
    public void Calculate_WhenCursorVisualRowChanges_MovesBubbleWithPrompt()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout upperLayout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 6,
            VisibleRows: 24,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180));
        CommandAssistAnchorLayout lowerLayout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 12,
            VisibleRows: 24,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180));

        Assert.True(lowerLayout.BubbleRect.Y > upperLayout.BubbleRect.Y);
        Assert.True(lowerLayout.PromptRect.Y > upperLayout.PromptRect.Y);
    }

    [Fact]
    public void Calculate_WhenPaneIsNarrow_UsesCompactBubbleLayout()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 420,
            PaneHeight: 420,
            CellHeight: 18,
            CursorVisualRow: 12,
            VisibleRows: 20,
            BubbleWidth: 420,
            BubbleHeight: 36,
            PopupWidth: 520,
            PopupHeight: 180));

        Assert.True(layout.UseCompactBubbleLayout);
    }

    [Fact]
    public void Calculate_WhenBubbleWidthIsTight_UsesCompactBubbleLayout()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 700,
            PaneHeight: 420,
            CellHeight: 18,
            CursorVisualRow: 12,
            VisibleRows: 20,
            BubbleWidth: 300,
            BubbleHeight: 36,
            PopupWidth: 380,
            PopupHeight: 180));

        Assert.True(layout.UseCompactBubbleLayout);
    }

    // ------------------------------------------------------------------ mark anchor (V2 Phase 2a)
    //
    // The mark anchor is a known prompt row rather than a guessed one, so these cases assert two
    // things at once: that the row is honoured exactly, and that none of the band ratios that hedge
    // the guess get a say. Several are written as a mark/no-mark pair against otherwise identical
    // inputs, because "the mark changed the answer" is the property, and a single-layout assertion
    // can be satisfied by the heuristic happening to agree.

    [Fact]
    public void Calculate_WhenMarkAnchorIsMidViewport_AnchorsPromptToTheMarkRow()
    {
        var calculator = new CommandAssistAnchorCalculator();

        const int markRow = 12;
        const double cellHeight = 18;
        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 540,
            CellHeight: cellHeight,
            CursorVisualRow: 3,
            VisibleRows: 30,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasMarkAnchor: true,
            MarkVisualRow: markRow));

        Assert.True(layout.UsesMarkAnchor);
        Assert.True(layout.UsesPromptAnchor);
        Assert.Equal(markRow * cellHeight, layout.PromptRect.Top, precision: 1);
        Assert.True(layout.BubbleRect.Bottom <= layout.PromptRect.Top,
            $"Expected mark-anchored bubble bottom {layout.BubbleRect.Bottom} to clear mark row top {layout.PromptRect.Top}.");
        Assert.Equal(4, layout.PromptRect.Top - layout.BubbleRect.Bottom, precision: 1);
    }

    [Fact]
    public void Calculate_WhenMarkAnchorIsSet_IgnoresTheCursorRowEntirely()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorRequest WithCursorRow(int cursorRow) => new(
            PaneWidth: 960,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: cursorRow,
            VisibleRows: 30,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasMarkAnchor: true,
            MarkVisualRow: 12);

        CommandAssistAnchorLayout high = calculator.Calculate(WithCursorRow(0));
        CommandAssistAnchorLayout low = calculator.Calculate(WithCursorRow(29));

        Assert.Equal(high.PromptRect, low.PromptRect);
        Assert.Equal(high.BubbleRect, low.BubbleRect);
        Assert.Equal(high.PopupRect, low.PopupRect);
    }

    [Fact]
    public void Calculate_WhenMarkAnchorIsOnTopVisibleRow_PlacesBubbleBelowWithInputRowClearance()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 0,
            VisibleRows: 30,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasMarkAnchor: true,
            MarkVisualRow: 0));

        Assert.True(layout.UsesMarkAnchor);
        Assert.True(layout.BubbleRect.Top >= layout.PromptRect.Bottom,
            $"Expected bubble top {layout.BubbleRect.Top} to sit below mark row bottom {layout.PromptRect.Bottom} when nothing fits above.");
        // One input row of clearance: the mark points at the first row of the input, which wraps.
        Assert.Equal(layout.PromptRect.Height + 4, layout.BubbleRect.Top - layout.PromptRect.Bottom, precision: 1);
    }

    [Fact]
    public void Calculate_WhenMarkAnchorIsOnLastVisibleRow_FlipsBubbleAboveTheMark()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 29,
            VisibleRows: 30,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasMarkAnchor: true,
            MarkVisualRow: 29));

        Assert.True(layout.UsesMarkAnchor);
        Assert.True(layout.BubbleRect.Bottom <= layout.PromptRect.Top,
            $"Expected the bubble to flip above the bottom-row mark, but bubble bottom {layout.BubbleRect.Bottom} was past prompt top {layout.PromptRect.Top}.");
        AssertRectWithin(layout.BubbleRect, 960, 540);
        AssertRectWithin(layout.PopupRect, 960, 540);
    }

    [Fact]
    public void Calculate_WhenMarkAnchorIsInTheShortPaneUpperBand_PlacesAboveWhereTheHeuristicWouldPlaceBelow()
    {
        // The short-pane upper band (0.60 of pane height) exists to keep the overlay off login
        // banners when the prompt row is a guess. A mark says the row is a prompt, so the only
        // question left is whether the bubble fits above it - and here it does.
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorRequest Request(bool hasMarkAnchor) => new(
            PaneWidth: 900,
            PaneHeight: 220,
            CellHeight: 18,
            CursorVisualRow: 5,
            VisibleRows: 12,
            BubbleWidth: 360,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasMarkAnchor: hasMarkAnchor,
            MarkVisualRow: hasMarkAnchor ? 5 : -1);

        CommandAssistAnchorLayout marked = calculator.Calculate(Request(hasMarkAnchor: true));
        CommandAssistAnchorLayout heuristic = calculator.Calculate(Request(hasMarkAnchor: false));

        Assert.True(marked.UsesMarkAnchor);
        Assert.False(heuristic.UsesMarkAnchor);
        Assert.Equal(marked.PromptRect.Top, heuristic.PromptRect.Top, precision: 1);
        Assert.True(marked.BubbleRect.Bottom <= marked.PromptRect.Top,
            $"Expected the mark-anchored bubble above the prompt, but bubble bottom was {marked.BubbleRect.Bottom} against prompt top {marked.PromptRect.Top}.");
        Assert.True(heuristic.BubbleRect.Top >= heuristic.PromptRect.Bottom,
            $"Expected the heuristic band to push the bubble below the prompt, but bubble top was {heuristic.BubbleRect.Top}.");
    }

    [Fact]
    public void Calculate_WhenPaneIsShortButWideAndMarkAnchored_StillUsesSideFloatingPopup()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 960,
            PaneHeight: 220,
            CellHeight: 18,
            CursorVisualRow: 8,
            VisibleRows: 12,
            BubbleWidth: 320,
            BubbleHeight: 36,
            PopupWidth: 360,
            PopupHeight: 180,
            HasMarkAnchor: true,
            MarkVisualRow: 8));

        Assert.True(layout.UsesMarkAnchor);
        Assert.Equal(CommandAssistPopupDirection.RightSide, layout.PopupDirection);
        Assert.True(layout.PopupRect.Left >= layout.BubbleRect.Right);
    }

    [Fact]
    public void Calculate_WhenPaneIsNarrowAndMarkAnchored_KeepsCompactBubbleLayoutAndClamps()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 420,
            PaneHeight: 420,
            CellHeight: 18,
            CursorVisualRow: 12,
            VisibleRows: 20,
            BubbleWidth: 420,
            BubbleHeight: 36,
            PopupWidth: 520,
            PopupHeight: 180,
            HasMarkAnchor: true,
            MarkVisualRow: 12));

        Assert.True(layout.UsesMarkAnchor);
        Assert.True(layout.UseCompactBubbleLayout);
        AssertRectWithin(layout.BubbleRect, 420, 420);
        AssertRectWithin(layout.PopupRect, 420, 420);
        AssertRectWithin(layout.PromptRect, 420, 420);
    }

    /// <summary>
    /// A mark that scrolled out of the viewport, aged out of history, or came from a dead
    /// coordinate generation reaches the calculator the same way: as no mark at all. The App layer
    /// resolves those cases (see <c>ShellMarkAnchorResolverTests</c>); the contract here is only
    /// that their absence restores the heuristic rather than leaving the overlay unplaced.
    /// </summary>
    [Fact]
    public void Calculate_WhenMarkAnchorIsAbsent_FallsBackToTheCursorHeuristic()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 900,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 18,
            VisibleRows: 24,
            BubbleWidth: 380,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasReliablePromptAnchor: false,
            HasMarkAnchor: false,
            MarkVisualRow: -1));

        Assert.False(layout.UsesMarkAnchor);
        Assert.False(layout.UsesPromptAnchor);
        Assert.Equal(18 * 18.0, layout.PromptRect.Top, precision: 1);
    }

    [Fact]
    public void Calculate_WhenMarkRowIsNegative_FallsBackToTheCursorHeuristic()
    {
        var calculator = new CommandAssistAnchorCalculator();

        CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
            PaneWidth: 900,
            PaneHeight: 540,
            CellHeight: 18,
            CursorVisualRow: 6,
            VisibleRows: 24,
            BubbleWidth: 380,
            BubbleHeight: 36,
            PopupWidth: 460,
            PopupHeight: 180,
            HasMarkAnchor: true,
            MarkVisualRow: -1));

        Assert.False(layout.UsesMarkAnchor);
        Assert.True(layout.UsesPromptAnchor);
        Assert.Equal(6 * 18.0, layout.PromptRect.Top, precision: 1);
    }

    private static void AssertRectWithin(AssistRect rect, double paneWidth, double paneHeight)
    {
        Assert.True(rect.X >= 0, $"Expected rect.X >= 0 but was {rect.X}.");
        Assert.True(rect.Y >= 0, $"Expected rect.Y >= 0 but was {rect.Y}.");
        Assert.True(rect.Right <= paneWidth, $"Expected rect.Right <= {paneWidth} but was {rect.Right}.");
        Assert.True(rect.Bottom <= paneHeight, $"Expected rect.Bottom <= {paneHeight} but was {rect.Bottom}.");
    }
}
