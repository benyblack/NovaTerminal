using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using System.Collections.ObjectModel;
using NovaTerminal.CommandAssist.Application;
using NovaTerminal.Controls;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.CommandAssist.Views;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistLayoutTests
{
    [Fact]
    public void CommandAssistBarViewModel_MapsCompactBubbleState()
    {
        var vm = new CommandAssistBarViewModel
        {
            IsVisible = true,
            ModeLabel = "Suggest",
            QueryText = "git ",
            TopSuggestionText = "git status"
        };

        Assert.True(vm.Bubble.IsVisible);
        Assert.Equal("Suggest", vm.Bubble.ModeLabel);
        Assert.Equal("git ", vm.Bubble.QueryText);
        Assert.Equal("git status", vm.Bubble.SummaryText);
    }

    [Fact]
    public void CommandAssistBarViewModel_WhenPopupIsClosed_DoesNotForceHelperPopupVisible()
    {
        var vm = new CommandAssistBarViewModel
        {
            IsVisible = true,
            ModeLabel = "Fix",
            QueryText = "gti status",
            TopSuggestionText = "git status",
            HasSuggestions = true,
            IsPopupOpen = false
        };

        Assert.True(vm.Bubble.IsVisible);
        Assert.False(vm.Popup.IsVisible);
    }

    [AvaloniaFact]
    public void TerminalPane_HostsBubbleAndPopupAsOverlayViews()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        var bubbleView = pane.FindControl<CommandAssistBubbleView>("CommandAssistBubble");
        var popupView = pane.FindControl<CommandAssistPopupView>("CommandAssistPopup");
        var commandAssistBar = pane.FindControl<CommandAssistBarView>("CommandAssistBar");

        Assert.NotNull(bubbleView);
        Assert.NotNull(popupView);
        Assert.Null(commandAssistBar);
        Assert.Equal(0, Grid.GetRow(bubbleView));
        Assert.Equal(0, Grid.GetRow(popupView));
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenHelpModeIsOpen_BindsBubbleAndPopupState()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("Get-ChildItem");
        pane.OpenCommandAssistHelp();
        await Task.Delay(50);

        var bubbleView = pane.FindControl<CommandAssistBubbleView>("CommandAssistBubble");
        var popupView = pane.FindControl<CommandAssistPopupView>("CommandAssistPopup");
        Assert.NotNull(bubbleView);
        Assert.NotNull(popupView);

        var bubbleVm = Assert.IsType<CommandAssistBubbleViewModel>(bubbleView.DataContext);
        var popupVm = Assert.IsType<CommandAssistPopupViewModel>(popupView.DataContext);

        var bubbleModeLabel = bubbleView.FindControl<TextBlock>("BubbleModeLabelText");
        var popupDescription = popupView.FindControl<TextBlock>("PopupSelectedDescriptionTextBlock");

        Assert.NotNull(bubbleModeLabel);
        Assert.NotNull(popupDescription);
        Assert.Equal("Help", bubbleModeLabel.Text);
        Assert.True(bubbleVm.IsVisible);
        Assert.True(popupVm.IsVisible);
        Assert.Equal("Help", popupVm.ModeLabel);
        Assert.False(string.IsNullOrWhiteSpace(popupVm.SelectedDescriptionText));
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenHelperModeHasNoSuggestions_ShowsPopupEmptyState()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("frobnicate");
        pane.OpenCommandAssistHelp();
        await Task.Delay(50);

        CommandAssistPopupView popupView = Assert.IsType<CommandAssistPopupView>(pane.FindControl<CommandAssistPopupView>("CommandAssistPopup"));
        CommandAssistPopupViewModel vm = Assert.IsType<CommandAssistPopupViewModel>(popupView.DataContext);
        var emptyState = popupView.FindControl<TextBlock>("PopupEmptyStateTextBlock");

        Assert.True(vm.IsVisible);
        Assert.True(vm.ShowEmptyState);
        Assert.Equal("No local help found.", vm.EmptyStateText);
        Assert.NotNull(emptyState);
        Assert.Equal("No local help found.", emptyState.Text);
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenSuggestModeIsCollapsed_KeepsPopupHidden()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("git st");
        await Task.Delay(50);

        CommandAssistBubbleView bubbleView = Assert.IsType<CommandAssistBubbleView>(pane.FindControl<CommandAssistBubbleView>("CommandAssistBubble"));
        CommandAssistPopupView popupView = Assert.IsType<CommandAssistPopupView>(pane.FindControl<CommandAssistPopupView>("CommandAssistPopup"));
        CommandAssistBubbleViewModel bubbleVm = Assert.IsType<CommandAssistBubbleViewModel>(bubbleView.DataContext);
        CommandAssistPopupViewModel popupVm = Assert.IsType<CommandAssistPopupViewModel>(popupView.DataContext);

        Assert.True(bubbleVm.IsVisible);
        Assert.False(popupVm.IsVisible);
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenPaneIsNarrow_HidesBubbleQueryText()
    {
        var pane = new TerminalPane
        {
            Width = 420,
            Height = 420
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(420, 420));
        pane.Arrange(new Rect(0, 0, 420, 420));
        pane.NotifyCommandAssistPaste("git status --short");
        await Task.Delay(50);
        pane.Measure(new Size(420, 420));
        pane.Arrange(new Rect(0, 0, 420, 420));

        CommandAssistBubbleView bubbleView = Assert.IsType<CommandAssistBubbleView>(pane.FindControl<CommandAssistBubbleView>("CommandAssistBubble"));
        var queryText = bubbleView.FindControl<TextBlock>("BubbleQueryText");

        Assert.NotNull(queryText);
        Assert.False(queryText.IsVisible);
    }

    [AvaloniaFact]
    public void TerminalPane_WhenPaneIsMediumWidth_UsesReducedOverlayWidths()
    {
        var pane = new TerminalPane
        {
            Width = 700,
            Height = 420
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(700, 420));
        pane.Arrange(new Rect(0, 0, 700, 420));

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.True(layout.BubbleRect.Width < 420);
        Assert.True(layout.PopupRect.Width < 520);
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenPaneIsShort_ConstrainsPopupHeight()
    {
        var pane = new TerminalPane
        {
            Width = 700,
            Height = 220
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(700, 220));
        pane.Arrange(new Rect(0, 0, 700, 220));
        pane.NotifyCommandAssistPaste("Get-ChildItem");
        pane.OpenCommandAssistHelp();
        await Task.Delay(50);
        pane.Measure(new Size(700, 220));
        pane.Arrange(new Rect(0, 0, 700, 220));

        CommandAssistPopupView popupView = Assert.IsType<CommandAssistPopupView>(pane.FindControl<CommandAssistPopupView>("CommandAssistPopup"));

        Assert.True(popupView.MaxHeight > 0);
        Assert.True(popupView.MaxHeight < 220);
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenBubbleIsVisible_KeepsBubbleAbovePromptAnchor()
    {
        var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));
        pane.NotifyCommandAssistPaste("git st");
        await Task.Delay(50);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));

        CommandAssistBubbleView bubbleView = Assert.IsType<CommandAssistBubbleView>(pane.FindControl<CommandAssistBubbleView>("CommandAssistBubble"));
        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.True(layout.BubbleRect.Bottom <= layout.PromptRect.Top,
            $"Expected layout bubble bottom {layout.BubbleRect.Bottom} to clear prompt top {layout.PromptRect.Top}.");
        Assert.Equal(layout.BubbleRect.Top, bubbleView.Margin.Top, precision: 1);
        double bubbleBottom = bubbleView.Margin.Top + bubbleView.Height;
        Assert.True(bubbleBottom <= layout.PromptRect.Top,
            $"Expected bubble bottom {bubbleBottom} to clear prompt top {layout.PromptRect.Top}.");
    }

    [AvaloniaFact]
    public void TerminalPane_WhenRemoteShellIsNotIntegrated_UsesFallbackAnchorInsteadOfPromptAnchor()
    {
        var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        ConfigureCommandAssist(pane);
        pane.UpdateProfile(new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Command = "ssh.exe",
            SshHost = "ubuntu.example"
        });
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));

        TerminalView termView = Assert.IsType<TerminalView>(pane.FindControl<TerminalView>("TermView"));
        Assert.NotNull(pane.Buffer);
        termView.Measure(new Size(900, 500));
        termView.Arrange(new Rect(0, 0, 900, 500));
        termView.SetMetricsForTest(10, 18);
        pane.Buffer.SetCursorPosition(0, 1);

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Bottom > 500 * 0.5,
            $"Expected fallback bubble to stay in the lower safe zone, but bottom was {layout.BubbleRect.Bottom}.");
    }

    [AvaloniaFact]
    public void TerminalPane_WhenRemoteShellCursorIsSettledLow_UsesCursorBandFallbackNearInput()
    {
        var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        ConfigureCommandAssist(pane);
        pane.UpdateProfile(new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Command = "ssh.exe",
            SshHost = "ubuntu.example"
        });
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));

        TerminalView termView = Assert.IsType<TerminalView>(pane.FindControl<TerminalView>("TermView"));
        Assert.NotNull(pane.Buffer);
        termView.Measure(new Size(900, 500));
        termView.Arrange(new Rect(0, 0, 900, 500));
        termView.SetMetricsForTest(10, 18);
        pane.Buffer.SetCursorPosition(0, 18);

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Bottom < 500 * 0.8,
            $"Expected settled SSH fallback bubble to move near the input band, but bottom was {layout.BubbleRect.Bottom}.");
        Assert.True(layout.BubbleRect.Bottom <= layout.PromptRect.Top,
            $"Expected settled SSH fallback bubble bottom {layout.BubbleRect.Bottom} to clear prompt top {layout.PromptRect.Top}.");
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenPopupIsVisible_AnchorsPopupTopToCalculatedRect()
    {
        var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));
        pane.NotifyCommandAssistPaste("Get-ChildItem");
        pane.OpenCommandAssistHelp();
        await Task.Delay(50);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));

        CommandAssistPopupView popupView = Assert.IsType<CommandAssistPopupView>(pane.FindControl<CommandAssistPopupView>("CommandAssistPopup"));
        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.Equal(layout.PopupRect.Top, popupView.Margin.Top, precision: 1);
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenPopupIsNarrow_UsesCompactPopupLayout()
    {
        var pane = new TerminalPane
        {
            Width = 420,
            Height = 420
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(420, 420));
        pane.Arrange(new Rect(0, 0, 420, 420));
        pane.NotifyCommandAssistPaste("Get-ChildItem");
        pane.OpenCommandAssistHelp();
        await Task.Delay(50);
        pane.Measure(new Size(420, 420));
        pane.Arrange(new Rect(0, 0, 420, 420));

        CommandAssistPopupView popupView = Assert.IsType<CommandAssistPopupView>(pane.FindControl<CommandAssistPopupView>("CommandAssistPopup"));
        CommandAssistPopupViewModel vm = Assert.IsType<CommandAssistPopupViewModel>(popupView.DataContext);
        Border detailPanel = Assert.IsType<Border>(popupView.FindControl<Border>("PopupDetailPanel"));

        Assert.True(vm.UseCompactLayout);
        Assert.False(detailPanel.IsVisible);
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenAssistBecomesVisible_DoesNotChangeTerminalRowHeight()
    {
        var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));

        var terminalView = pane.FindControl<NovaTerminal.Core.TerminalView>("TermView");
        Assert.NotNull(terminalView);
        double baselineHeight = terminalView.Bounds.Height;

        pane.NotifyCommandAssistPaste("Get-ChildItem");
        pane.OpenCommandAssistHelp();
        await Task.Delay(50);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));

        Assert.Equal(baselineHeight, terminalView.Bounds.Height);
    }

    [AvaloniaFact]
    public void CommandAssistBubbleView_BindsCollapsedState()
    {
        var vm = new CommandAssistBubbleViewModel
        {
            IsVisible = true,
            ModeLabel = "Suggest",
            QueryText = "git st",
            SummaryText = "git status"
        };
        var view = new CommandAssistBubbleView
        {
            DataContext = vm
        };

        var modeLabel = view.FindControl<TextBlock>("BubbleModeLabelText");
        var summary = view.FindControl<TextBlock>("BubbleSummaryText");

        Assert.NotNull(modeLabel);
        Assert.NotNull(summary);
        Assert.True(view.IsVisible);
        Assert.Equal("Suggest", modeLabel.Text);
        Assert.Equal("git status", summary.Text);
    }

    [AvaloniaFact]
    public void CommandAssistPopupView_BindsResultListAndDetailState()
    {
        var suggestions = new ObservableCollection<CommandAssistSuggestionItemViewModel>
        {
            new(
                SelectionGlyph: ">",
                DisplayText: "git status",
                DescriptionText: "Show working tree state.",
                BadgesText: "History",
                MetadataText: @"C:\repo",
                IsSelected: true,
                Type: AssistSuggestionType.History)
        };
        var vm = new CommandAssistPopupViewModel(suggestions)
        {
            IsVisible = true,
            ModeLabel = "Help",
            TopSuggestionText = "git status",
            SelectedDescriptionText = "Show working tree state.",
            HasSuggestions = true
        };
        var view = new CommandAssistPopupView
        {
            DataContext = vm
        };

        var modeLabel = view.FindControl<TextBlock>("PopupModeLabelText");
        var description = view.FindControl<TextBlock>("PopupSelectedDescriptionTextBlock");
        var list = view.FindControl<ItemsControl>("PopupSuggestionsList");

        Assert.NotNull(modeLabel);
        Assert.NotNull(description);
        Assert.NotNull(list);
        Assert.True(view.IsVisible);
        Assert.Equal("Help", modeLabel.Text);
        Assert.Equal("Show working tree state.", description.Text);
        Assert.Single(vm.Suggestions);
    }

    [AvaloniaFact]
    public void CommandAssistPopupView_WhenCompactLayout_HidesDetailPane()
    {
        var suggestions = new ObservableCollection<CommandAssistSuggestionItemViewModel>
        {
            new(
                SelectionGlyph: ">",
                DisplayText: "git status",
                DescriptionText: "Show working tree state.",
                BadgesText: "History",
                MetadataText: @"C:\repo",
                IsSelected: true,
                Type: AssistSuggestionType.History)
        };
        var vm = new CommandAssistPopupViewModel(suggestions)
        {
            IsVisible = true,
            ModeLabel = "Help",
            TopSuggestionText = "git status",
            SelectedDescriptionText = "Show working tree state.",
            HasSuggestions = true,
            UseCompactLayout = true
        };
        var view = new CommandAssistPopupView
        {
            DataContext = vm
        };

        Border detailPanel = Assert.IsType<Border>(view.FindControl<Border>("PopupDetailPanel"));

        Assert.False(detailPanel.IsVisible);
    }

    [AvaloniaFact]
    public void CommandAssistPopupView_CanExistWithoutTerminalGridRowHost()
    {
        var vm = new CommandAssistPopupViewModel(new ObservableCollection<CommandAssistSuggestionItemViewModel>())
        {
            IsVisible = true,
            ModeLabel = "History",
            EmptyStateText = "No local help found.",
            ShowEmptyState = true
        };
        var view = new CommandAssistPopupView
        {
            DataContext = vm
        };

        var emptyState = view.FindControl<TextBlock>("PopupEmptyStateTextBlock");

        Assert.NotNull(emptyState);
        Assert.Equal(0, Grid.GetRow(view));
        Assert.Equal("No local help found.", emptyState.Text);
    }

    private static void ConfigureCommandAssist(TerminalPane pane)
    {
        var settings = TerminalSettings.Load();
        settings.CommandAssistEnabled = true;
        settings.CommandAssistHistoryEnabled = true;
        pane.ApplySettings(settings);
    }
}
