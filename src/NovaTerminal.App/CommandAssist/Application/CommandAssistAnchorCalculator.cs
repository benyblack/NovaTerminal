using System;
using Avalonia;

namespace NovaTerminal.CommandAssist.Application;

public sealed class CommandAssistAnchorCalculator
{
    private const double PanePadding = 12;
    private const double PromptBubbleGap = 4;
    private const double BubblePopupGap = 8;
    private const double HorizontalGap = 12;
    private const double MinimumPromptWidth = 120;
    private const double CompactBubbleWidthThreshold = 320;
    private const double CompactPaneWidthThreshold = 560;
    private const double UnreliableCursorBandStartRatio = 0.55;
    private const int UnreliableCursorBandMinVisibleRows = 8;
    private const double PromptUpperBandRatio = 0.45;

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
        bool usesPromptAnchor = request.HasReliablePromptAnchor &&
                                request.VisibleRows > 0 &&
                                request.CursorVisualRow >= 0 &&
                                request.CellHeight > 0;

        Rect promptRect = usesPromptAnchor
            ? CreatePromptRect(request, paneWidth, paneHeight, promptHeight)
            : CreateFallbackPromptRect(request, paneWidth, paneHeight, promptHeight);

        Rect bubbleRect = usesPromptAnchor
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

        CommandAssistPopupDirection popupDirection;
        double popupX;
        double popupY;

        if (canPlacePopupUpward)
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

        Rect popupRect = CreatePopupRect(
            popupDirection,
            popupX,
            popupY,
            popupWidth,
            popupHeight,
            bubbleRect,
            paneWidth,
            paneHeight);

        return new CommandAssistAnchorLayout(promptRect, bubbleRect, popupRect, popupDirection, usesPromptAnchor, useCompactBubbleLayout);
    }

    private static Rect CreatePromptRect(
        CommandAssistAnchorRequest request,
        double paneWidth,
        double paneHeight,
        double promptHeight)
    {
        double promptWidth = Math.Min(Math.Max(MinimumPromptWidth, request.BubbleWidth * 0.5), paneWidth - (PanePadding * 2));
        double promptY = PanePadding + (request.CursorVisualRow * request.CellHeight);
        Rect promptRect = new(PanePadding, promptY, promptWidth, promptHeight);
        return ClampRect(promptRect, paneWidth, paneHeight);
    }

    private static Rect CreateFallbackPromptRect(
        CommandAssistAnchorRequest request,
        double paneWidth,
        double paneHeight,
        double promptHeight)
    {
        double promptWidth = Math.Min(Math.Max(MinimumPromptWidth, request.BubbleWidth * 0.5), paneWidth - (PanePadding * 2));
        double promptY = ShouldUseUnreliableCursorBandFallback(request)
            ? PanePadding + (request.CursorVisualRow * request.CellHeight)
            : paneHeight - PanePadding - promptHeight;
        return ClampRect(new Rect(PanePadding, promptY, promptWidth, promptHeight), paneWidth, paneHeight);
    }

    private static Rect CreateBubbleAdjacentToPrompt(
        Rect promptRect,
        double bubbleWidth,
        double bubbleHeight,
        double paneWidth,
        double paneHeight)
    {
        double bubbleX = promptRect.X;
        double desiredAboveY = promptRect.Top - PromptBubbleGap - bubbleHeight;
        double clampedAboveY = Math.Max(PanePadding, desiredAboveY);
        bool isPromptInUpperStartupBand = promptRect.Top <= paneHeight * PromptUpperBandRatio;
        double bubbleY = isPromptInUpperStartupBand
            ? promptRect.Bottom + PromptBubbleGap
            : clampedAboveY;

        if (bubbleY + bubbleHeight > paneHeight - PanePadding)
        {
            bubbleY = clampedAboveY;
        }

        return ClampRect(new Rect(bubbleX, bubbleY, bubbleWidth, bubbleHeight), paneWidth, paneHeight);
    }

    private static Rect CreateFallbackBubbleRect(
        Rect promptRect,
        double bubbleWidth,
        double bubbleHeight,
        double paneWidth,
        double paneHeight)
    {
        // Keep fallback behavior aligned with prompt-anchored behavior:
        // if the prompt sits in the upper startup band, place the bubble below it.
        return CreateBubbleAdjacentToPrompt(promptRect, bubbleWidth, bubbleHeight, paneWidth, paneHeight);
    }

    private static Rect CreatePopupRect(
        CommandAssistPopupDirection popupDirection,
        double popupX,
        double popupY,
        double popupWidth,
        double popupHeight,
        Rect bubbleRect,
        double paneWidth,
        double paneHeight)
    {
        if (popupDirection == CommandAssistPopupDirection.Downward)
        {
            double minTop = bubbleRect.Bottom + BubblePopupGap;
            double maxBottom = paneHeight - PanePadding;
            double height = Math.Max(1, Math.Min(popupHeight, maxBottom - minTop));
            return ClampRect(new Rect(popupX, minTop, popupWidth, height), paneWidth, paneHeight);
        }

        if (popupDirection == CommandAssistPopupDirection.Upward)
        {
            double maxBottom = bubbleRect.Top - BubblePopupGap;
            double top = Math.Max(PanePadding, maxBottom - popupHeight);
            double height = Math.Max(1, maxBottom - top);
            return ClampRect(new Rect(popupX, top, popupWidth, height), paneWidth, paneHeight);
        }

        return ClampRect(new Rect(popupX, popupY, popupWidth, popupHeight), paneWidth, paneHeight);
    }

    private static Rect ClampRect(Rect rect, double paneWidth, double paneHeight)
    {
        double maxWidth = Math.Max(1, paneWidth - (PanePadding * 2));
        double maxHeight = Math.Max(1, paneHeight - (PanePadding * 2));
        double width = Math.Min(rect.Width, maxWidth);
        double height = Math.Min(rect.Height, maxHeight);
        double x = Math.Clamp(rect.X, PanePadding, Math.Max(PanePadding, paneWidth - PanePadding - width));
        double y = Math.Clamp(rect.Y, PanePadding, Math.Max(PanePadding, paneHeight - PanePadding - height));
        return new Rect(x, y, width, height);
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
    bool HasReliablePromptAnchor = true);

public sealed record CommandAssistAnchorLayout(
    Rect PromptRect,
    Rect BubbleRect,
    Rect PopupRect,
    CommandAssistPopupDirection PopupDirection,
    bool UsesPromptAnchor,
    bool UseCompactBubbleLayout);

public enum CommandAssistPopupDirection
{
    Upward,
    Downward,
    RightSide,
    LeftSide,
}
