using NovaTerminal.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using System.Collections.ObjectModel;
using NovaTerminal.CommandAssist.Application;
using NovaTerminal.Controls;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.CommandAssist.Views;
using NovaTerminal.Platform;
using NovaTerminal.VT;

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

    /// <summary>
    /// The assist surfaces are overlays in row 0, not a footer bar that steals a terminal row.
    /// </summary>
    /// <remarks>
    /// This used to also assert <c>FindControl&lt;CommandAssistBarView&gt;("CommandAssistBar")</c>
    /// returned null - a guard against the pre-M4.2 footer bar coming back. Phase 0b deleted
    /// <c>CommandAssistBarView</c> outright, so the guard is now enforced by the compiler: the type
    /// does not exist to be hosted.
    /// </remarks>
    [AvaloniaFact]
    public void TerminalPane_HostsBubbleAndPopupAsOverlayViews()
    {
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        var bubbleView = pane.FindControl<CommandAssistBubbleView>("CommandAssistBubble");
        var popupView = pane.FindControl<CommandAssistPopupView>("CommandAssistPopup");

        Assert.NotNull(bubbleView);
        Assert.NotNull(popupView);
        Assert.Equal(0, Grid.GetRow(bubbleView));
        Assert.Equal(0, Grid.GetRow(popupView));
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenHelpModeIsOpen_BindsBubbleAndPopupState()
    {
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        await AtAnIntegratedPromptAsync(pane, "Get-ChildItem");
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
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        await AtAnIntegratedPromptAsync(pane, "frobnicate");
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
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        await AtAnIntegratedPromptAsync(pane, "git st");
        await Task.Delay(50);

        CommandAssistBubbleView bubbleView = Assert.IsType<CommandAssistBubbleView>(pane.FindControl<CommandAssistBubbleView>("CommandAssistBubble"));
        CommandAssistPopupView popupView = Assert.IsType<CommandAssistPopupView>(pane.FindControl<CommandAssistPopupView>("CommandAssistPopup"));
        CommandAssistBubbleViewModel bubbleVm = Assert.IsType<CommandAssistBubbleViewModel>(bubbleView.DataContext);
        CommandAssistPopupViewModel popupVm = Assert.IsType<CommandAssistPopupViewModel>(popupView.DataContext);

        Assert.Equal(!string.IsNullOrWhiteSpace(bubbleVm.SummaryText), bubbleVm.IsVisible);
        Assert.False(popupVm.IsVisible);
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenPaneIsNarrow_HidesBubbleQueryText()
    {
        using var pane = new TerminalPane
        {
            Width = 420,
            Height = 420
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(420, 420));
        pane.Arrange(new Rect(0, 0, 420, 420));
        await AtAnIntegratedPromptAsync(pane, "git status --short");
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
        using var pane = new TerminalPane
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
        using var pane = new TerminalPane
        {
            Width = 700,
            Height = 220
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(700, 220));
        pane.Arrange(new Rect(0, 0, 700, 220));
        await AtAnIntegratedPromptAsync(pane, "Get-ChildItem");
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
        using var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));
        await AtAnIntegratedPromptAsync(pane, "git st");
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
        using var pane = new TerminalPane
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
        // Metrics before layout, not after (#232): arranging TermView resizes the buffer to
        // Bounds.Height / CellHeight, so pinning afterwards leaves the row count - and therefore any
        // cursor row this test sets - derived from the ambient font instead of from these numbers.
        termView.SetMetricsForTest(10, 18);
        termView.Measure(new Size(900, 500));
        termView.Arrange(new Rect(0, 0, 900, 500));
        pane.Buffer.SetCursorPosition(0, 1);

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Bottom > 500 * 0.5,
            $"Expected fallback bubble to stay in the lower safe zone, but bottom was {layout.BubbleRect.Bottom}.");
    }

    [AvaloniaFact]
    public void TerminalPane_WhenRemoteShellCursorIsSettledLow_UsesCursorBandFallbackNearInput()
    {
        using var pane = new TerminalPane
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
        // See the note on metric ordering above (#232). Here it mattered doubly: at the ambient cell
        // height this machine produces, the buffer was 17 rows and row 18 was silently clamped to 16.
        termView.SetMetricsForTest(10, 18);
        termView.Measure(new Size(900, 500));
        termView.Arrange(new Rect(0, 0, 900, 500));
        pane.Buffer.SetCursorPosition(0, 18);

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Bottom < 500 * 0.8,
            $"Expected settled SSH fallback bubble to move near the input band, but bottom was {layout.BubbleRect.Bottom}.");
        Assert.True(layout.BubbleRect.Bottom <= layout.PromptRect.Top,
            $"Expected settled SSH fallback bubble bottom {layout.BubbleRect.Bottom} to clear prompt top {layout.PromptRect.Top}.");
    }

    [AvaloniaFact]
    public void TerminalPane_WhenSshStartupHasTransientTermViewBounds_UsesPaneBoundsForFallbackLayout()
    {
        using var pane = new TerminalPane
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
        termView.SetMetricsForTest(10, 18);
        termView.Measure(new Size(900, 120));
        termView.Arrange(new Rect(0, 0, 900, 120));
        pane.Buffer.SetCursorPosition(0, 1);

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Bottom > 500 * 0.5,
            $"Expected startup SSH fallback bubble to stay in the lower safe zone despite transient term bounds, but bottom was {layout.BubbleRect.Bottom}.");
    }

    [AvaloniaFact]
    public void TerminalPane_WhenRemotePromptIsInUpperBandOnShortPane_SuppressesConservativeAssistLayout()
    {
        using var pane = new TerminalPane
        {
            Width = 900,
            Height = 220
        };
        ConfigureCommandAssist(pane);
        pane.UpdateProfile(new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Command = "ssh.exe",
            SshHost = "ubuntu.example"
        });
        pane.Measure(new Size(900, 220));
        pane.Arrange(new Rect(0, 0, 900, 220));

        TerminalView termView = Assert.IsType<TerminalView>(pane.FindControl<TerminalView>("TermView"));
        Assert.NotNull(pane.Buffer);
        // See the note on metric ordering above (#232).
        termView.SetMetricsForTest(10, 18);
        termView.Measure(new Size(900, 220));
        termView.Arrange(new Rect(0, 0, 900, 220));
        pane.Buffer.SetCursorPosition(0, 1);

        // The suppression decision is `cursorRow / (visibleRows - 1) < 0.55`, so it turns entirely on
        // these two numbers. Asserting them makes an environment that produces different ones fail
        // here, with the numbers, instead of further down as a confusing null layout.
        AssertPromptHint(termView, expectedCursorRow: 1, expectedVisibleRows: 12);

        CommandAssistAnchorLayout? layout = pane.CalculateCommandAssistAnchorLayoutForTest();

        Assert.Null(layout);
    }

    [AvaloniaFact]
    public void TerminalPane_WhenRemotePromptSettlesLowOnShortPane_ResumesConservativeAssistLayout()
    {
        using var pane = new TerminalPane
        {
            Width = 900,
            Height = 220
        };
        ConfigureCommandAssist(pane);
        pane.UpdateProfile(new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Command = "ssh.exe",
            SshHost = "ubuntu.example"
        });
        pane.Measure(new Size(900, 220));
        pane.Arrange(new Rect(0, 0, 900, 220));

        TerminalView termView = Assert.IsType<TerminalView>(pane.FindControl<TerminalView>("TermView"));
        Assert.NotNull(pane.Buffer);
        // #232: this is the test that was red locally and green in CI. Arranging TermView resizes the
        // buffer to Bounds.Height / CellHeight, so with metrics pinned *after* the arrange the row
        // count came from the ambient font. On this machine that gave 7 rows, row 7 was clamped to 6,
        // and 6/11 = 0.545 falls just under the 0.55 band-start ratio - suppressed, layout null. On CI
        // the row count was larger, row 7 survived, 7/11 = 0.636 cleared the ratio. A margin of 0.005
        // between passing and failing, decided by a font.
        termView.SetMetricsForTest(10, 18);
        termView.Measure(new Size(900, 220));
        termView.Arrange(new Rect(0, 0, 900, 220));
        pane.Buffer.SetCursorPosition(0, 7);

        // 220 / 18 = 12 rows, cursor row 7, so 7/11 = 0.636 - above the ratio, hence not suppressed.
        AssertPromptHint(termView, expectedCursorRow: 7, expectedVisibleRows: 12);

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.False(layout.UsesPromptAnchor);
    }

    [AvaloniaFact]
    public void TerminalPane_WhenRemotePromptHintRowsLagPaneHeight_UsesPaneEstimatedRowsForConservativeFallback()
    {
        using var pane = new TerminalPane
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
        termView.SetMetricsForTest(10, 18);
        termView.Measure(new Size(900, 160));
        termView.Arrange(new Rect(0, 0, 900, 160));
        pane.Buffer.SetCursorPosition(0, 5);

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Bottom > 500 * 0.5,
            $"Expected conservative fallback to stay in lower safe zone when prompt hint rows lag pane height, but bottom was {layout.BubbleRect.Bottom}.");
    }

    [AvaloniaFact]
    public async Task TerminalPane_WhenPopupIsVisible_AnchorsPopupTopToCalculatedRect()
    {
        using var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));
        await AtAnIntegratedPromptAsync(pane, "Get-ChildItem");
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
        using var pane = new TerminalPane
        {
            Width = 420,
            Height = 420
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(420, 420));
        pane.Arrange(new Rect(0, 0, 420, 420));
        await AtAnIntegratedPromptAsync(pane, "Get-ChildItem");
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
        using var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));

        var terminalView = pane.FindControl<NovaTerminal.Shell.TerminalView>("TermView");
        Assert.NotNull(terminalView);
        double baselineHeight = terminalView.Bounds.Height;

        await AtAnIntegratedPromptAsync(pane, "Get-ChildItem");
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

    /// <summary>
    /// Asserts the prompt hint the anchor calculation will actually read.
    /// </summary>
    /// <remarks>
    /// #232: the suppression rule is <c>cursorRow / (visibleRows - 1) &lt; 0.55</c>, and both numbers
    /// come from TermView's arranged height divided by its cell height. Pinning the metrics keeps them
    /// deterministic; asserting them here means a future change to how rows are derived fails with the
    /// numbers rather than as an unexplained null layout twenty lines later.
    /// </remarks>
    private static void AssertPromptHint(TerminalView termView, int expectedCursorRow, int expectedVisibleRows)
    {
        CommandAssistPromptHint? hint = termView.GetCommandAssistPromptHint();
        Assert.NotNull(hint);
        Assert.Equal(expectedVisibleRows, hint!.Value.VisibleRows);
        Assert.Equal(expectedCursorRow, hint.Value.VisibleCursorVisualRow);
    }

    /// <summary>
    /// Puts <paramref name="commandLine"/> on the pane's command line the way a shell does: an
    /// integrated prompt, the <c>OSC 133;B</c> mark, then the text.
    /// </summary>
    /// <remarks>
    /// Phase 1c deleted the shadow keystroke buffer, so <c>NotifyCommandAssistPaste</c> - which
    /// these tests used to seed a query with - no longer writes query state, and a paste-seeded
    /// help lookup would silently find nothing. The grid is the only source now, so the setup says
    /// so.
    /// </remarks>
    private static async Task AtAnIntegratedPromptAsync(TerminalPane pane, string commandLine)
    {
        pane.ArmShellIntegrationTracker();
        pane.CreateAndWireParser();
        pane.Parser!.Process("\x1b]133;A\x07PS C:\\> \x1b]133;B\x07" + commandLine);

        // The shell-integration event dispatcher is serialized and asynchronous; the controller's
        // lifecycle gate opens when B reaches it through that queue.
        await Task.Delay(50);
    }

    private static void ConfigureCommandAssist(TerminalPane pane)
    {
        // Phase 0b: the pane no longer reaches for a static locator, so the services instance is
        // injected the same way MainWindow injects it in production.
        pane.CommandAssistServices = TestCommandAssistServices.Instance;

        // Constructed, not TerminalSettings.Load() (#232). Load() reads the *developer's* settings
        // file, so the font family and size a test runs against were whatever this machine happened to
        // have configured - 18pt here, 14pt on a CI runner with no settings file at all. That is the
        // mechanism behind "fails locally, green in CI", and it applied to every test in this file.
        var settings = new TerminalSettings
        {
            CommandAssistEnabled = true,
            CommandAssistHistoryEnabled = true
        };
        pane.ApplySettings(settings);
    }
}
