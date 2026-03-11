using System;
using Avalonia;

namespace NovaTerminal.CommandAssist.Application;

public sealed class CommandAssistAnchorCalculator
{
    private const double PanePadding = 12;
    private const double VerticalGap = 8;
    private const double MinimumPromptWidth = 120;

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
        bool useCompactBubbleLayout = paneWidth <= 480 || request.BubbleWidth > availableWidth;
        bool usesPromptAnchor = request.HasReliablePromptAnchor &&
                                request.VisibleRows > 0 &&
                                request.CursorVisualRow >= 0 &&
                                request.CellHeight > 0;

        Rect promptRect = usesPromptAnchor
            ? CreatePromptRect(request, paneWidth, paneHeight, promptHeight)
            : CreateFallbackPromptRect(paneWidth, paneHeight, promptHeight);

        Rect bubbleRect = usesPromptAnchor
            ? CreateBubbleAbovePrompt(promptRect, bubbleWidth, bubbleHeight, paneWidth, paneHeight)
            : CreateFallbackBubbleRect(bubbleWidth, bubbleHeight, paneWidth, paneHeight);

        double upwardTop = bubbleRect.Top - VerticalGap - popupHeight;
        bool canPlacePopupUpward = upwardTop >= PanePadding;
        CommandAssistPopupDirection popupDirection = canPlacePopupUpward
            ? CommandAssistPopupDirection.Upward
            : CommandAssistPopupDirection.Downward;

        double popupY = popupDirection == CommandAssistPopupDirection.Upward
            ? upwardTop
            : bubbleRect.Bottom + VerticalGap;
        double popupX = bubbleRect.X;
        Rect popupRect = ClampRect(new Rect(popupX, popupY, popupWidth, popupHeight), paneWidth, paneHeight);

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

    private static Rect CreateFallbackPromptRect(double paneWidth, double paneHeight, double promptHeight)
    {
        double promptWidth = Math.Min(Math.Max(MinimumPromptWidth, paneWidth * 0.35), paneWidth - (PanePadding * 2));
        double promptY = paneHeight - PanePadding - promptHeight;
        return ClampRect(new Rect(PanePadding, promptY, promptWidth, promptHeight), paneWidth, paneHeight);
    }

    private static Rect CreateBubbleAbovePrompt(
        Rect promptRect,
        double bubbleWidth,
        double bubbleHeight,
        double paneWidth,
        double paneHeight)
    {
        double bubbleX = promptRect.X;
        double bubbleY = promptRect.Top - VerticalGap - bubbleHeight;
        return ClampRect(new Rect(bubbleX, bubbleY, bubbleWidth, bubbleHeight), paneWidth, paneHeight);
    }

    private static Rect CreateFallbackBubbleRect(
        double bubbleWidth,
        double bubbleHeight,
        double paneWidth,
        double paneHeight)
    {
        double bubbleX = paneWidth - PanePadding - bubbleWidth;
        double bubbleY = paneHeight - PanePadding - bubbleHeight;
        return ClampRect(new Rect(bubbleX, bubbleY, bubbleWidth, bubbleHeight), paneWidth, paneHeight);
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
}
