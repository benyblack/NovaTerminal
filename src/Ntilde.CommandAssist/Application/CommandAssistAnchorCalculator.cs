using System;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// Turns "where is the prompt" into bubble/popup rectangles.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two anchor sources, in priority order.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Mark anchor</b> (<see cref="CommandAssistAnchorRequest.HasMarkAnchor"/>): an
/// <c>OSC 133;B</c> mark resolved to a viewport row by the App layer. The row is a fact, so the
/// band ratios below do not apply to it — they exist to hedge a guess. Placement is a plain
/// geometric fit: above the prompt when the bubble fits there, flipped below when it does not.
/// </description></item>
/// <item><description>
/// <b>Cursor heuristic</b> (<see cref="CommandAssistAnchorRequest.CursorVisualRow"/> plus
/// <see cref="CommandAssistAnchorRequest.HasReliablePromptAnchor"/>): the pre-V2 behaviour, kept
/// verbatim for un-instrumented sessions. The cursor row is only *probably* the prompt row, so
/// the band ratios keep the overlay off startup banners and prompt-redraw noise.
/// </description></item>
/// </list>
/// <para>
/// Everything downstream of the anchor row — size clamps, the compact-layout thresholds, and the
/// popup flip/side rules — is shared by both sources.
/// </para>
/// </remarks>
public sealed class CommandAssistAnchorCalculator
{
    private const double PanePadding = 12;
    private const double PromptBubbleGap = 4;
    private const double BubblePopupGap = 8;
    private const double HorizontalGap = 12;
    private const double MinimumPromptWidth = 120;
    private const double CompactBubbleWidthThreshold = 320;
    private const double CompactPaneWidthThreshold = 560;

    /// <summary>
    /// Below this bubble width the shortcut hint drops its verbs and keeps its key names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The middle rung the UX-polish round added between "full hint" and "no hint". The compact
    /// threshold at 320 was the only step there was, so every width from 321 px upwards got the full
    /// ~200 px strip - and the bubble the owner was actually looking at is in that range, which is why
    /// his suggestion was ellipsised to six characters while a legend for keys he already knew kept
    /// its full width.
    /// </para>
    /// <para>
    /// 420 is the point at which the strip, the mode label and a query stop leaving the content
    /// column its 150 px floor. Above it everything fits and nothing needs to give.
    /// </para>
    /// </remarks>
    private const double TerseBubbleHintWidthThreshold = 420;
    // Band ratios. Every one of these is a hedge against not knowing where the prompt is; a
    // mark-anchored request bypasses all of them (see the class remarks). They survive because
    // markless sessions still exist -- an un-instrumented remote, a shell with integration
    // disabled -- not because the mark path needs a threshold anywhere.
    private const double UnreliableCursorBandStartRatio = 0.55;
    private const int UnreliableCursorBandMinVisibleRows = 8;
    private const double PromptUpperBandRatio = 0.45;
    private const double ReliableShortPanePromptUpperBandRatio = 0.60;
    private const double FallbackShortPanePromptUpperBandRatio = 0.70;
    private const double FallbackShortPaneHeightThreshold = 300;
    private const int StartupBandInputRowClearanceRows = 1;
    private const double PromptVerticalOffset = 0;

    public CommandAssistAnchorLayout Calculate(CommandAssistAnchorRequest request)
    {
        double paneWidth = Math.Max(1, request.PaneWidth);
        double paneHeight = Math.Max(1, request.PaneHeight);
        double availableWidth = Math.Max(1, paneWidth - (PanePadding * 2));
        double availableHeight = Math.Max(1, paneHeight - (PanePadding * 2));
        double promptHeight = Math.Max(1, request.CellHeight);
        double bubbleWidth = Math.Min(Math.Max(1, request.BubbleWidth), availableWidth);
        double bubbleHeight = Math.Min(Math.Max(1, request.BubbleHeight), availableHeight);
        double popupWidth = Math.Min(Math.Max(1, request.PopupWidth), availableWidth);
        double popupHeight = Math.Min(Math.Max(1, request.PopupHeight), availableHeight);
        bool useCompactBubbleLayout = paneWidth <= CompactPaneWidthThreshold ||
                                      request.BubbleWidth > availableWidth ||
                                      bubbleWidth <= CompactBubbleWidthThreshold;

        // The middle rung. Only meaningful when the hint is rendered at all, so it is deliberately
        // not or-ed with the compact decision: compact already hides the strip outright.
        bool useTerseBubbleHint = !useCompactBubbleLayout &&
                                  bubbleWidth <= TerseBubbleHintWidthThreshold;
        bool usesMarkAnchor = request.HasMarkAnchor &&
                              request.VisibleRows > 0 &&
                              request.MarkVisualRow >= 0 &&
                              request.CellHeight > 0;
        bool usesPromptAnchor = usesMarkAnchor ||
                                (request.HasReliablePromptAnchor &&
                                 request.VisibleRows > 0 &&
                                 request.CursorVisualRow >= 0 &&
                                 request.CellHeight > 0);

        AssistRect promptRect = usesPromptAnchor
            ? CreatePromptRect(
                request,
                usesMarkAnchor ? request.MarkVisualRow : request.CursorVisualRow,
                paneWidth,
                paneHeight,
                promptHeight)
            : CreateFallbackPromptRect(request, paneWidth, paneHeight, promptHeight);

        AssistRect bubbleRect = usesMarkAnchor
            ? CreateBubbleAdjacentToMark(promptRect, bubbleWidth, bubbleHeight, paneWidth, paneHeight)
            : usesPromptAnchor
                ? CreateBubbleAdjacentToPrompt(promptRect, bubbleWidth, bubbleHeight, paneWidth, paneHeight)
                : CreateFallbackBubbleRect(promptRect, bubbleWidth, bubbleHeight, paneWidth, paneHeight);

        double spaceAbove = Math.Max(0, bubbleRect.Top - BubblePopupGap - PanePadding);
        double spaceBelow = Math.Max(0, paneHeight - PanePadding - (bubbleRect.Bottom + BubblePopupGap));
        double upwardTop = bubbleRect.Top - BubblePopupGap - popupHeight;
        double downwardTop = bubbleRect.Bottom + BubblePopupGap;
        bool canPlacePopupUpward = upwardTop >= PanePadding;
        bool canPlacePopupDownward = downwardTop + popupHeight <= paneHeight - PanePadding;
        bool canPlacePopupRight = bubbleRect.Right + HorizontalGap + popupWidth <= paneWidth - PanePadding;
        bool canPlacePopupLeft = bubbleRect.Left - HorizontalGap - popupWidth >= PanePadding;
        bool hasMeaningfulVerticalRoom = Math.Max(spaceAbove, spaceBelow) >= popupHeight * 0.75;
        bool bubbleSitsBelowPrompt = bubbleRect.Top >= promptRect.Bottom;

        CommandAssistPopupDirection popupDirection;
        double popupX;
        double popupY;

        if (canPlacePopupUpward && canPlacePopupDownward)
        {
            popupDirection = bubbleSitsBelowPrompt
                ? CommandAssistPopupDirection.Downward
                : CommandAssistPopupDirection.Upward;
            popupX = bubbleRect.X;
            popupY = popupDirection == CommandAssistPopupDirection.Downward
                ? downwardTop
                : upwardTop;
        }
        else if (canPlacePopupUpward)
        {
            popupDirection = CommandAssistPopupDirection.Upward;
            popupX = bubbleRect.X;
            popupY = upwardTop;
        }
        else if (canPlacePopupDownward)
        {
            popupDirection = CommandAssistPopupDirection.Downward;
            popupX = bubbleRect.X;
            popupY = downwardTop;
        }
        else if (!hasMeaningfulVerticalRoom && canPlacePopupRight && bubbleRect.Top < promptRect.Top)
        {
            popupDirection = CommandAssistPopupDirection.RightSide;
            popupX = bubbleRect.Right + HorizontalGap;
            popupY = bubbleRect.Top;
        }
        else if (!hasMeaningfulVerticalRoom && canPlacePopupLeft && bubbleRect.Top < promptRect.Top)
        {
            popupDirection = CommandAssistPopupDirection.LeftSide;
            popupX = bubbleRect.Left - HorizontalGap - popupWidth;
            popupY = bubbleRect.Top;
        }
        else
        {
            popupDirection = upwardTop >= PanePadding * 2
                ? CommandAssistPopupDirection.Upward
                : CommandAssistPopupDirection.Downward;
            popupX = bubbleRect.X;
            popupY = popupDirection == CommandAssistPopupDirection.Upward
                ? upwardTop
                : downwardTop;
        }

        AssistRect popupRect = CreatePopupRect(
            popupDirection,
            popupX,
            popupY,
            popupWidth,
            popupHeight,
            bubbleRect,
            paneWidth,
            paneHeight);

        return new CommandAssistAnchorLayout(
            promptRect,
            bubbleRect,
            popupRect,
            popupDirection,
            usesPromptAnchor,
            useCompactBubbleLayout,
            usesMarkAnchor,
            useTerseBubbleHint);
    }

    private static AssistRect CreatePromptRect(
        CommandAssistAnchorRequest request,
        int anchorVisualRow,
        double paneWidth,
        double paneHeight,
        double promptHeight)
    {
        double promptWidth = Math.Min(Math.Max(MinimumPromptWidth, request.BubbleWidth * 0.5), paneWidth - (PanePadding * 2));
        double promptY = PromptVerticalOffset + (anchorVisualRow * request.CellHeight);
        AssistRect promptRect = new(PanePadding, promptY, promptWidth, promptHeight);
        return ClampRect(promptRect, paneWidth, paneHeight);
    }

    /// <summary>
    /// Bubble placement for a known prompt row: above it when the bubble fits above, flipped
    /// below when it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No band ratio is consulted. The upper-band rule on the heuristic path exists because a
    /// cursor near the top of the pane might be a login banner still printing rather than a
    /// prompt; a mark says which it is, so the only question left is whether the bubble fits.
    /// </para>
    /// <para>
    /// Above is preferred because it never covers the row the user is typing on and never covers
    /// the rows the shell is about to print into. The one-row clearance below is kept for the
    /// flipped case: the mark points at the <i>first</i> row of the input, which can wrap onto the
    /// next one.
    /// </para>
    /// </remarks>
    private static AssistRect CreateBubbleAdjacentToMark(
        AssistRect promptRect,
        double bubbleWidth,
        double bubbleHeight,
        double paneWidth,
        double paneHeight)
    {
        double desiredAboveY = promptRect.Top - PromptBubbleGap - bubbleHeight;
        double belowPromptY = promptRect.Bottom + PromptBubbleGap;
        double guardedBelowPromptY = belowPromptY + (promptRect.Height * StartupBandInputRowClearanceRows);
        double bubbleY;

        if (desiredAboveY >= PanePadding)
        {
            bubbleY = desiredAboveY;
        }
        else if (guardedBelowPromptY + bubbleHeight <= paneHeight - PanePadding)
        {
            bubbleY = guardedBelowPromptY;
        }
        else if (belowPromptY + bubbleHeight <= paneHeight - PanePadding)
        {
            bubbleY = belowPromptY;
        }
        else
        {
            // Neither side fits outright (a very short pane): stay above and let the clamp decide.
            bubbleY = Math.Max(PanePadding, desiredAboveY);
        }

        return ClampRect(new AssistRect(promptRect.X, bubbleY, bubbleWidth, bubbleHeight), paneWidth, paneHeight);
    }

    private static AssistRect CreateFallbackPromptRect(
        CommandAssistAnchorRequest request,
        double paneWidth,
        double paneHeight,
        double promptHeight)
    {
        double promptWidth = Math.Min(Math.Max(MinimumPromptWidth, request.BubbleWidth * 0.5), paneWidth - (PanePadding * 2));
        double promptY = ShouldUseUnreliableCursorBandFallback(request)
            ? PromptVerticalOffset + (request.CursorVisualRow * request.CellHeight)
            : paneHeight - PanePadding - promptHeight;
        return ClampRect(new AssistRect(PanePadding, promptY, promptWidth, promptHeight), paneWidth, paneHeight);
    }

    private static AssistRect CreateBubbleAdjacentToPrompt(
        AssistRect promptRect,
        double bubbleWidth,
        double bubbleHeight,
        double paneWidth,
        double paneHeight)
    {
        double upperBandRatio = paneHeight <= FallbackShortPaneHeightThreshold
            ? ReliableShortPanePromptUpperBandRatio
            : PromptUpperBandRatio;
        return CreateBubbleAdjacentToPrompt(
            promptRect,
            bubbleWidth,
            bubbleHeight,
            paneWidth,
            paneHeight,
            upperBandRatio,
            reserveInputRowClearance: true);
    }

    private static AssistRect CreateBubbleAdjacentToPrompt(
        AssistRect promptRect,
        double bubbleWidth,
        double bubbleHeight,
        double paneWidth,
        double paneHeight,
        double upperBandRatio,
        bool reserveInputRowClearance)
    {
        double bubbleX = promptRect.X;
        double desiredAboveY = promptRect.Top - PromptBubbleGap - bubbleHeight;
        double clampedAboveY = Math.Max(PanePadding, desiredAboveY);
        bool isPromptInUpperStartupBand = promptRect.Top <= paneHeight * upperBandRatio;

        double belowPromptY = promptRect.Bottom + PromptBubbleGap;
        double guardedBelowPromptY = belowPromptY + (promptRect.Height * StartupBandInputRowClearanceRows);
        double bubbleY = isPromptInUpperStartupBand
            ? (reserveInputRowClearance ? guardedBelowPromptY : belowPromptY)
            : clampedAboveY;

        if (isPromptInUpperStartupBand &&
            reserveInputRowClearance &&
            bubbleY + bubbleHeight > paneHeight - PanePadding)
        {
            bubbleY = belowPromptY;
        }

        if (bubbleY + bubbleHeight > paneHeight - PanePadding)
        {
            bubbleY = clampedAboveY;
        }

        return ClampRect(new AssistRect(bubbleX, bubbleY, bubbleWidth, bubbleHeight), paneWidth, paneHeight);
    }

    private static AssistRect CreateFallbackBubbleRect(
        AssistRect promptRect,
        double bubbleWidth,
        double bubbleHeight,
        double paneWidth,
        double paneHeight)
    {
        // Unreliable prompt anchors (for conservative SSH mode) should prefer below-placement
        // more aggressively on short panes to avoid covering startup/login text.
        double upperBandRatio = paneHeight <= FallbackShortPaneHeightThreshold
            ? FallbackShortPanePromptUpperBandRatio
            : PromptUpperBandRatio;
        return CreateBubbleAdjacentToPrompt(
            promptRect,
            bubbleWidth,
            bubbleHeight,
            paneWidth,
            paneHeight,
            upperBandRatio,
            reserveInputRowClearance: false);
    }

    private static AssistRect CreatePopupRect(
        CommandAssistPopupDirection popupDirection,
        double popupX,
        double popupY,
        double popupWidth,
        double popupHeight,
        AssistRect bubbleRect,
        double paneWidth,
        double paneHeight)
    {
        if (popupDirection == CommandAssistPopupDirection.Downward)
        {
            double minTop = bubbleRect.Bottom + BubblePopupGap;
            double maxBottom = paneHeight - PanePadding;
            double height = Math.Max(1, Math.Min(popupHeight, maxBottom - minTop));
            return ClampRect(new AssistRect(popupX, minTop, popupWidth, height), paneWidth, paneHeight);
        }

        if (popupDirection == CommandAssistPopupDirection.Upward)
        {
            double maxBottom = bubbleRect.Top - BubblePopupGap;
            double top = Math.Max(PanePadding, maxBottom - popupHeight);
            double height = Math.Max(1, maxBottom - top);
            return ClampRect(new AssistRect(popupX, top, popupWidth, height), paneWidth, paneHeight);
        }

        return ClampRect(new AssistRect(popupX, popupY, popupWidth, popupHeight), paneWidth, paneHeight);
    }

    private static AssistRect ClampRect(AssistRect rect, double paneWidth, double paneHeight)
    {
        double maxWidth = Math.Max(1, paneWidth - (PanePadding * 2));
        double maxHeight = Math.Max(1, paneHeight - (PanePadding * 2));
        double width = Math.Min(rect.Width, maxWidth);
        double height = Math.Min(rect.Height, maxHeight);
        double x = Math.Clamp(rect.X, PanePadding, Math.Max(PanePadding, paneWidth - PanePadding - width));
        double y = Math.Clamp(rect.Y, PanePadding, Math.Max(PanePadding, paneHeight - PanePadding - height));
        return new AssistRect(x, y, width, height);
    }

    private static bool ShouldUseUnreliableCursorBandFallback(CommandAssistAnchorRequest request)
    {
        if (request.VisibleRows < UnreliableCursorBandMinVisibleRows || request.CursorVisualRow < 0 || request.CellHeight <= 0)
        {
            return false;
        }

        double normalizedCursorRow = request.CursorVisualRow / (double)(request.VisibleRows - 1);
        return normalizedCursorRow >= UnreliableCursorBandStartRatio;
    }
}

/// <param name="CursorVisualRow">
/// Viewport row of the cursor. The heuristic anchor source; ignored when
/// <paramref name="HasMarkAnchor"/> is set.
/// </param>
/// <param name="HasReliablePromptAnchor">
/// Whether the cursor row may be treated as the prompt row. Only consulted on the heuristic path.
/// </param>
/// <param name="HasMarkAnchor">
/// True when an <c>OSC 133;B</c> mark resolved to a row inside the viewport. Takes priority over
/// the cursor heuristic and bypasses every band threshold.
/// </param>
/// <param name="MarkVisualRow">Viewport row of that mark; the first row of the user's input.</param>
public sealed record CommandAssistAnchorRequest(
    double PaneWidth,
    double PaneHeight,
    double CellHeight,
    int CursorVisualRow,
    int VisibleRows,
    double BubbleWidth,
    double BubbleHeight,
    double PopupWidth,
    double PopupHeight,
    bool HasReliablePromptAnchor = true,
    bool HasMarkAnchor = false,
    int MarkVisualRow = -1);

/// <param name="UsesPromptAnchor">
/// True when the layout is anchored to a prompt row at all — from either source. A mark anchor
/// always sets this; see <paramref name="UsesMarkAnchor"/> to tell the two apart.
/// </param>
/// <param name="UsesMarkAnchor">
/// True when the anchor row came from an <c>OSC 133;B</c> mark. The App layer keys its
/// SSH-conservative behaviour off this: a mark-anchored layout runs no suppression check, no
/// placement-correction passes, and no opacity games.
/// </param>
public sealed record CommandAssistAnchorLayout(
    AssistRect PromptRect,
    AssistRect BubbleRect,
    AssistRect PopupRect,
    CommandAssistPopupDirection PopupDirection,
    bool UsesPromptAnchor,
    bool UseCompactBubbleLayout,
    bool UsesMarkAnchor = false,
    bool UseTerseBubbleHint = false);

public enum CommandAssistPopupDirection
{
    Upward,
    Downward,
    RightSide,
    LeftSide,
}
