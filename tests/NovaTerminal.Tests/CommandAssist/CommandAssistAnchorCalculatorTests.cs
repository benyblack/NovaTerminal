using Avalonia;
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

    private static void AssertRectWithin(Rect rect, double paneWidth, double paneHeight)
    {
        Assert.True(rect.X >= 0, $"Expected rect.X >= 0 but was {rect.X}.");
        Assert.True(rect.Y >= 0, $"Expected rect.Y >= 0 but was {rect.Y}.");
        Assert.True(rect.Right <= paneWidth, $"Expected rect.Right <= {paneWidth} but was {rect.Right}.");
        Assert.True(rect.Bottom <= paneHeight, $"Expected rect.Bottom <= {paneHeight} but was {rect.Bottom}.");
    }
}
