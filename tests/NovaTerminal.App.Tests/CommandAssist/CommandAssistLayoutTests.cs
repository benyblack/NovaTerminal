using NovaTerminal.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    /// <summary>
    /// The fish-style split: when the suggestion extends the query, the bubble shows the query and
    /// only the <em>tail</em> of the suggestion, which read together are the whole thing.
    /// </summary>
    /// <remarks>
    /// This used to assert the summary was the full "git status" - i.e. that the bubble rendered
    /// "git " and then "git status" beside it, repeating the three characters the user had already
    /// typed and spending the width the round is trying to recover. See
    /// <see cref="CommandAssistBubbleViewModel.IsSummaryContinuation"/>.
    /// </remarks>
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
        Assert.True(vm.Bubble.ShowQueryText);
        Assert.True(vm.Bubble.IsSummaryContinuation);
        Assert.Equal("status", vm.Bubble.SummaryText);
    }

    /// <summary>
    /// A suggestion that is not an extension of the query keeps its whole text in the summary.
    /// </summary>
    [Fact]
    public void CommandAssistBarViewModel_WhenTheSuggestionDoesNotExtendTheQuery_KeepsTheWholeSummary()
    {
        var vm = new CommandAssistBarViewModel
        {
            IsVisible = true,
            ModeLabel = "Suggest",
            QueryText = "gti",
            TopSuggestionText = "git status"
        };

        Assert.False(vm.Bubble.IsSummaryContinuation);
        Assert.Equal("git status", vm.Bubble.SummaryText);
    }

    /// <summary>
    /// With the query column suppressed by the compact layout, the summary reverts to the full
    /// suggestion: a bare tail with its head hidden would be gibberish.
    /// </summary>
    [Fact]
    public void CommandAssistBarViewModel_WhenTheQueryIsHidden_DoesNotShowOnlyTheCompletionTail()
    {
        var vm = new CommandAssistBarViewModel
        {
            IsVisible = true,
            ModeLabel = "Suggest",
            QueryText = "git ",
            TopSuggestionText = "git status",
            AllowBubbleQueryText = false
        };

        Assert.False(vm.Bubble.ShowQueryText);
        Assert.False(vm.Bubble.IsSummaryContinuation);
        Assert.Equal("git status", vm.Bubble.SummaryText);
    }

    /// <summary>
    /// The Fix bubble leads with the suggestion, not with the command that failed.
    /// </summary>
    /// <remarks>
    /// The owner's screenshot read <c>Fix | print -l $precmd_functions | Di...</c>. The failed command
    /// is already on screen in the scrollback; the fix for it is the only new information the bubble
    /// carries, so it gets the width. See <c>CommandAssistBarViewModel.ApplyBubbleContent</c>.
    /// </remarks>
    [Fact]
    public void CommandAssistBarViewModel_InFixMode_DoesNotEchoTheFailedCommand()
    {
        var vm = new CommandAssistBarViewModel
        {
            IsVisible = true,
            ModeLabel = "Fix",
            QueryText = "gti status",
            TopSuggestionText = "Did you mean git?"
        };

        Assert.False(vm.Bubble.ShowQueryText);
        Assert.False(vm.Bubble.IsSummaryContinuation);
        Assert.Equal("Did you mean git?", vm.Bubble.SummaryText);
    }

    /// <summary>
    /// Exactly one surface at a time: opening the popup takes the bubble down.
    /// </summary>
    /// <remarks>
    /// The owner's screenshot had a "History | vim .env | Enter insert" bubble rendered below an open
    /// History popup whose top row was the same <c>vim .env</c>. Both surfaces were bound and both
    /// were visible, because the bubble followed <c>IsVisible</c> alone.
    /// </remarks>
    [Fact]
    public void CommandAssistBarViewModel_WhenThePopupIsOpen_HidesTheBubble()
    {
        var vm = new CommandAssistBarViewModel
        {
            IsVisible = true,
            ModeLabel = "History",
            TopSuggestionText = "vim .env",
            HasSuggestions = true,
            IsPopupOpen = true
        };

        Assert.True(vm.Popup.IsVisible);
        Assert.False(vm.Bubble.IsVisible);
    }

    /// <summary>
    /// The shortcut hint's middle rung: keys without verbs, so the suggestion keeps its width.
    /// </summary>
    [Fact]
    public void CommandAssistBarViewModel_WhenTheHintIsTerse_DropsTheVerbsAndKeepsTheKeys()
    {
        var vm = new CommandAssistBarViewModel
        {
            IsVisible = true,
            ModeLabel = "Suggest",
            BubbleHintDetail = AssistHintDetail.Terse
        };

        Assert.True(vm.Bubble.ShowShortcutHint);
        Assert.Equal("Up/Down  |  Ctrl+Enter  |  Esc", vm.Bubble.ShortcutHintText);

        // The popup has room and is unaffected: it is where the shortcuts are still taught.
        Assert.Equal(CommandAssistBarViewModel.IdleHintText, vm.Popup.ShortcutHintText);
    }

    [Fact]
    public void CommandAssistBarViewModel_WhenTheHintIsHidden_DropsTheStripEntirely()
    {
        var vm = new CommandAssistBarViewModel
        {
            IsVisible = true,
            ModeLabel = "Suggest",
            BubbleHintDetail = AssistHintDetail.Hidden
        };

        Assert.False(vm.Bubble.ShowShortcutHint);
        Assert.False(vm.Bubble.ShowIntegrationStatus);
    }

    /// <summary>
    /// The integration chip reports which capture mode the session is in.
    /// </summary>
    [Theory]
    [InlineData(true, "integrated")]
    [InlineData(false, "basic")]
    public void CommandAssistBarViewModel_PublishesTheIntegrationChip(bool isLive, string expected)
    {
        var vm = new CommandAssistBarViewModel
        {
            IsVisible = true,
            ModeLabel = "Suggest",
            IsShellIntegrationLive = isLive
        };

        Assert.Equal(expected, vm.Bubble.IntegrationStatusText);
        Assert.Equal(expected, vm.Popup.IntegrationStatusText);
        Assert.True(vm.Bubble.ShowIntegrationStatus);
        Assert.False(string.IsNullOrWhiteSpace(vm.Bubble.IntegrationStatusTooltip));
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

        // One surface at a time (UX-polish round, issue 6). Help opens the popup, so the bubble
        // stands down: it would otherwise render the same mode label, query and top suggestion the
        // popup is already showing in full, directly above it.
        Assert.False(bubbleVm.IsVisible);
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

    // ------------------------------------------- explicit intent is never hidden (V2 Phase 3a)

    /// <summary>
    /// The owner's third report: in a tab split into two SSH panes, the assist did not appear on one of
    /// them. A split halves the pane height, which puts both panes under the short-pane threshold, and
    /// on the pane whose prompt was still in the upper band the conservative band check hid the overlay
    /// outright - for <c>Ctrl+R</c> as readily as for an uninvited bubble.
    /// </summary>
    /// <remarks>
    /// The geometry is copied exactly from
    /// <c>TerminalPane_WhenRemotePromptIsInUpperBandOnShortPane_SuppressesConservativeAssistLayout</c>,
    /// which is the negative control: without an explicitly requested surface the same pane still
    /// returns null. The Escape at the end is a second control on the same instance, so the bypass
    /// cannot be satisfied by "a controller exists" rather than by the session state.
    /// </remarks>
    [AvaloniaFact]
    public void TerminalPane_WhenHistorySearchIsOpenOnAShortRemotePane_DoesNotSuppressTheOverlay()
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
        termView.SetMetricsForTest(10, 18);
        termView.Measure(new Size(900, 220));
        termView.Arrange(new Rect(0, 0, 900, 220));
        pane.Buffer.SetCursorPosition(0, 1);
        AssertPromptHint(termView, expectedCursorRow: 1, expectedVisibleRows: 12);

        // No marks are produced: this is a markless remote pane, the case the suppression exists for.
        Assert.True(pane.OpenCommandAssistHistorySearch());

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(
            pane.CalculateCommandAssistAnchorLayoutForTest());

        // Worst-case placement is the safe lower band, not invisibility.
        Assert.False(layout.UsesMarkAnchor);
        Assert.False(layout.UsesPromptAnchor);
        Assert.True(layout.BubbleRect.Bottom > 220 * 0.5,
            $"Expected the summoned surface to land in the lower safe band, but bottom was {layout.BubbleRect.Bottom}.");

        // Control: dismiss the surface and the conservative suppression is back.
        Assert.True(pane.TryHandleCommandAssistKey(Key.Escape, KeyModifiers.None));
        Assert.Null(pane.CalculateCommandAssistAnchorLayoutForTest());
    }

    /// <summary>
    /// The other half of the third report, checked directly rather than through geometry: two panes off
    /// one shared services graph each get their own initialized controller, and <c>Ctrl+R</c> opens on
    /// the second as readily as on the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>These are two standalone panes, not a split</strong> (PR #290 review, correcting an earlier
    /// version of this comment that said "both panes of a split"). There is no <c>MainWindow</c>, no split
    /// grid and no shared parent here - nothing about split *geometry* is under test, and the geometry
    /// half of the report is covered by the short-pane suppression tests above.
    /// </para>
    /// <para>
    /// What this does pin is the part a split would depend on: <c>MainWindow.WirePane</c> assigns
    /// <c>CommandAssistServices</c> to every pane it creates, including the ones a split adds, so the
    /// injection is structural - and nothing in a pane's own initialization may be single-instance. A
    /// second pane sharing the same history store and snippet store must build a second controller and a
    /// second view-model rather than finding the first one's state.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void TwoPanesSharingOneServicesGraph_BothOpenTheirOwnHistorySearch()
    {
        using var first = new TerminalPane();
        using var second = new TerminalPane();
        ConfigureCommandAssist(first);
        ConfigureCommandAssist(second);

        Assert.True(first.OpenCommandAssistHistorySearch());
        Assert.True(second.OpenCommandAssistHistorySearch());

        CommandAssistBarViewModel firstViewModel = Assert.IsType<CommandAssistBarViewModel>(first.CommandAssistViewModel);
        CommandAssistBarViewModel secondViewModel = Assert.IsType<CommandAssistBarViewModel>(second.CommandAssistViewModel);

        Assert.NotSame(firstViewModel, secondViewModel);
        Assert.True(firstViewModel.IsVisible);
        Assert.True(secondViewModel.IsVisible);
        Assert.True(firstViewModel.IsPopupOpen);
        Assert.True(secondViewModel.IsPopupOpen);

        // And they are independent: dismissing one leaves the other up, which is what "one pane of the
        // split has no assist" would have looked like from the other direction.
        Assert.True(first.TryHandleCommandAssistKey(Key.Escape, KeyModifiers.None));

        Assert.False(firstViewModel.IsVisible);
        Assert.True(secondViewModel.IsVisible);
    }

    // ------------------------------------------------------------ mark anchoring (V2 Phase 2a)
    //
    // These are the SSH counterparts to the conservative-fallback tests above. The difference is
    // one OSC 133;B: with a mark the remote prompt row is known, so the whole conservative stack -
    // the unreliable-anchor fallback, the short-pane suppression band, the placement-correction
    // passes - has to step aside. Without one, every assertion above still holds.

    [AvaloniaFact]
    public void TerminalPane_WhenRemoteShellEmitsMarks_AnchorsToTheMarkRow()
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
        // Metrics before layout, not after (#232) - see the note on the conservative tests above.
        termView.SetMetricsForTest(10, 18);
        termView.Measure(new Size(900, 500));
        termView.Arrange(new Rect(0, 0, 900, 500));
        MarkPromptAt(pane, row: 10);

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.True(layout.UsesMarkAnchor);
        Assert.True(layout.UsesPromptAnchor,
            "A mark-anchored SSH pane is prompt-anchored: the row is a fact, not a per-session-type guess.");
        Assert.Equal(180, layout.PromptRect.Top, precision: 1);
        Assert.True(layout.BubbleRect.Bottom <= layout.PromptRect.Top,
            $"Expected bubble bottom {layout.BubbleRect.Bottom} to clear the marked prompt row top {layout.PromptRect.Top}.");
    }

    [AvaloniaFact]
    public void TerminalPane_WhenRemoteShellEmitsMarksInUpperBandOnShortPane_DoesNotSuppressTheOverlay()
    {
        // Same geometry as TerminalPane_WhenRemotePromptIsInUpperBandOnShortPane_SuppressesConservativeAssistLayout,
        // which returns null. The only difference is the mark, and it is the whole difference.
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
        termView.SetMetricsForTest(10, 18);
        termView.Measure(new Size(900, 220));
        termView.Arrange(new Rect(0, 0, 900, 220));
        MarkPromptAt(pane, row: 1);

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.True(layout.UsesMarkAnchor);
        Assert.Equal(18, layout.PromptRect.Top, precision: 1);
    }

    [AvaloniaFact]
    public void TerminalPane_WhenTheMarkComesFromADeadCoordinateGeneration_FallsBackToTheHeuristic()
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
        termView.Measure(new Size(900, 500));
        termView.Arrange(new Rect(0, 0, 900, 500));
        MarkPromptAt(pane, row: 10);
        Assert.True(Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest()).UsesMarkAnchor);

        // CSI 3J - what clear(1) sends - resets the buffer's row counters, so the mark's
        // AbsoluteRow now names an unrelated row. The generation epoch is what notices.
        pane.Parser!.Process("\x1b[3J");

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.False(layout.UsesMarkAnchor);
        Assert.False(layout.UsesPromptAnchor);
    }

    /// <summary>
    /// The negative control for the zero-pass assertions below.
    /// </summary>
    /// <remarks>
    /// Without this test those assertions are vacuous. The correction stack only runs for a
    /// <i>visible</i> assist on an SSH pane, so a pane-level test that never makes the bound view
    /// model visible reads a zero counter whether the <c>UsesMarkAnchor</c> gate exists or not -
    /// deleting the gate leaves it green. This is the same pane, the same visible view model and
    /// the same real <c>UpdateCommandAssistOverlayPlacement</c> path as its mark-anchored twin,
    /// minus the mark, and it must reach the counter.
    /// </remarks>
    [AvaloniaFact]
    public void TerminalPane_WhenAMarklessSshLayoutIsPlacedWithTheAssistVisible_RunsPlacementCorrectionPasses()
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
        termView.Measure(new Size(900, 500));
        termView.Arrange(new Rect(0, 0, 900, 500));
        // Cursor low in the pane so the conservative fallback produces a layout rather than
        // suppressing it: the correction stack is only reachable when there is a layout to correct.
        pane.Buffer!.SetCursorPosition(0, 18);

        CommandAssistAnchorLayout markless = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());
        Assert.False(markless.UsesMarkAnchor);
        Assert.Equal(0, pane.CommandAssistPlacementCorrectionPassesForTest);

        ShowCommandAssist(pane);

        Assert.True(pane.CommandAssistPlacementCorrectionPassesForTest >= 1,
            "A visible assist on a markless SSH pane must still schedule the correction stack. If it " +
            "does not, this harness cannot reach the counter at all and the zero-pass assertions on " +
            "the mark-anchored panes prove nothing.");
    }

    [AvaloniaFact]
    public void TerminalPane_WhenLayoutIsMarkAnchored_RunsNoPlacementCorrectionPasses()
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
        termView.Measure(new Size(900, 500));
        termView.Arrange(new Rect(0, 0, 900, 500));
        MarkPromptAt(pane, row: 10);

        CommandAssistAnchorLayout marked = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());
        Assert.True(marked.UsesMarkAnchor);

        // The counter, driven the production way: same pane geometry and same visible view model as
        // TerminalPane_WhenAMarklessSshLayoutIsPlacedWithTheAssistVisible_RunsPlacementCorrectionPasses,
        // which reaches the counter. The mark is the only difference, so a zero here is the mark's
        // doing.
        ShowCommandAssist(pane);
        Assert.Equal(0, pane.CommandAssistPlacementCorrectionPassesForTest);

        // And the gate asked directly, which pins *why*: with the assist visible on an SSH pane - the
        // exact conditions the correction stack was written for - a mark-anchored layout declines it,
        // while the same layout without the mark flag still wants it.
        Assert.False(pane.ShouldCorrectCommandAssistPlacementForTest(marked, assistIsVisible: true));
        Assert.True(pane.ShouldCorrectCommandAssistPlacementForTest(marked with { UsesMarkAnchor = false }, assistIsVisible: true));
    }

    [AvaloniaFact]
    public void TerminalPane_WhenLayoutIsMarkAnchoredOnALocalPane_AlsoRunsNoPlacementCorrectionPasses()
    {
        using var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));

        TerminalView termView = Assert.IsType<TerminalView>(pane.FindControl<TerminalView>("TermView"));
        Assert.NotNull(pane.Buffer);
        termView.SetMetricsForTest(10, 18);
        termView.Measure(new Size(900, 500));
        termView.Arrange(new Rect(0, 0, 900, 500));
        MarkPromptAt(pane, row: 4);

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());

        Assert.True(layout.UsesMarkAnchor);
        ShowCommandAssist(pane);
        Assert.Equal(0, pane.CommandAssistPlacementCorrectionPassesForTest);

        // Two reasons hold this down on a local pane, not one: the correction stack is SSH-only as
        // well as markless-only. Asserting that the markless twin is *also* declined says so out
        // loud, so this does not read as mark evidence it cannot supply - the SSH pair above is where
        // the mark does the work on its own.
        Assert.False(pane.ShouldCorrectCommandAssistPlacementForTest(layout, assistIsVisible: true));
        Assert.False(pane.ShouldCorrectCommandAssistPlacementForTest(layout with { UsesMarkAnchor = false }, assistIsVisible: true));
    }

    /// <summary>
    /// The markless-to-mark handoff, with a placement-correction pass already in flight.
    /// </summary>
    /// <remarks>
    /// <c>ScheduleCommandAssistPlacementCorrection</c> posts a closure that captures the layout it
    /// was scheduled for. A <c>133;B</c> mark can land before that closure runs, and by then the
    /// overlay has been re-placed against the mark row - so measured against the captured markless
    /// layout it looks like a large drift, and the pass would hide the overlay and re-apply the
    /// markless margins for a frame at the exact moment the anchor became exact. The pass has to
    /// re-derive the layout and re-ask the gate.
    /// <para>
    /// The one test in this file hosted in a <see cref="Window"/>. The correction pass measures the
    /// rendered position with <c>TranslatePoint</c>, which needs both visuals attached to a visual
    /// root and returns <c>null</c> without one - and on <c>null</c> the pass bails before it can do
    /// any of the damage under test, so a detached pane makes this assertion vacuous. (Verified: it
    /// was, until the window went in.)
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void TerminalPane_WhenAMarkArrivesWhileACorrectionPassIsPending_TheStalePassLeavesTheMarkPlacementAlone()
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

        var window = new Window
        {
            Content = pane,
            Width = 960,
            Height = 560
        };
        try
        {
            window.Show();

            TerminalView termView = Assert.IsType<TerminalView>(pane.FindControl<TerminalView>("TermView"));
            Assert.NotNull(pane.Buffer);
            // Metrics before the layout that sizes the buffer, as everywhere else in this file (#232).
            termView.SetMetricsForTest(10, 18);
            pane.Measure(new Size(900, 500));
            pane.Arrange(new Rect(0, 0, 900, 500));
            termView.Measure(new Size(900, 500));
            termView.Arrange(new Rect(0, 0, 900, 500));
            pane.Buffer!.SetCursorPosition(0, 18);

            CommandAssistBarViewModel vm = ShowCommandAssist(pane);
            CommandAssistAnchorLayout markless = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());
            Assert.False(markless.UsesMarkAnchor);
            Assert.True(pane.CommandAssistPlacementCorrectionPassesForTest >= 1,
                "The handoff is only reachable with a correction pass in flight.");

            // The handoff. Row 2 rather than row 18 so the two anchors disagree by far more than the
            // 2px drift tolerance; the QueryText change stands in for the repaint that re-runs
            // placement in production (a bound-property change is what the pane listens to).
            MarkPromptAt(pane, row: 2);
            vm.QueryText = "git st";

            CommandAssistAnchorLayout marked = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());
            Assert.True(marked.UsesMarkAnchor);
            Assert.True(Math.Abs(marked.BubbleRect.Y - markless.BubbleRect.Y) > 2,
                $"The mark and markless anchors must actually disagree for this to test anything, but they were {marked.BubbleRect.Y} and {markless.BubbleRect.Y}.");

            CommandAssistBubbleView bubbleView = Assert.IsType<CommandAssistBubbleView>(pane.FindControl<CommandAssistBubbleView>("CommandAssistBubble"));
            Grid overlayHost = Assert.IsType<Grid>(pane.FindControl<Grid>("CommandAssistOverlayHost"));
            Assert.Equal(marked.BubbleRect.Y, bubbleView.Margin.Top, precision: 1);
            Assert.True(overlayHost.IsVisible, "The pending pass bails on a hidden host, which would make this vacuous.");

            // Lay the mark-anchored margins out, then confirm the pending pass really can measure the
            // rendered position - the drift branch is unreachable if it cannot, and an unreachable
            // branch cannot be asserted about. A margin change invalidates the bubble only and lets
            // the layout manager walk up from there, so re-measuring the pane by hand short-circuits
            // at the first still-valid ancestor and changes nothing; the real layout pass is needed.
            RelayoutTo(pane, bubbleView, new Size(900, 500));
            Assert.True(bubbleView.IsVisible);
            Point renderedTopLeft = Assert.IsType<Point>(bubbleView.TranslatePoint(new Point(0, 0), pane));
            Assert.Equal(marked.BubbleRect.Y, renderedTopLeft.Y, precision: 1);

            // The damage is one frame wide, and it self-heals: an ungated pass suppresses the overlay
            // and re-applies the markless margins, then posts a placement update that - being
            // mark-anchored - unwinds both. Draining the queue and looking at the end state therefore
            // cannot see it. Record the trail instead: "the overlay never went transparent, and the
            // bubble never visited the markless row" is the property, not the resting position.
            var opacityTrail = new List<double>();
            var bubbleTopTrail = new List<double>();
            overlayHost.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.OpacityProperty)
                {
                    opacityTrail.Add((double)e.NewValue!);
                }
            };
            bubbleView.PropertyChanged += (_, e) =>
            {
                if (e.Property == Layoutable.MarginProperty)
                {
                    bubbleTopTrail.Add(((Thickness)e.NewValue!).Top);
                }
            };

            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(0.0, opacityTrail);
            Assert.DoesNotContain(bubbleTopTrail, top => Math.Abs(top - markless.BubbleRect.Y) <= 2);
            Assert.Equal(marked.BubbleRect.Y, bubbleView.Margin.Top, precision: 1);
            Assert.Equal(1.0, overlayHost.Opacity);
        }
        finally
        {
            window.Close();
        }
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

    // ------------------------- the hint strip may not squeeze the bubble (PR #290 review)

    /// <summary>
    /// 280 px is the bubble-width floor <c>CalculateCommandAssistSurfaceSizing</c> clamps to, which is
    /// what a split SSH pane lands on. The suggestion must keep a readable column there whether or not
    /// the hint strip is on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this test used to assert, and why it was the wrong shape.</strong> It measured
    /// that <em>collapsing</em> the hint gave the summary its width back, with a negative control
    /// requiring the un-collapsed case to be less than half as wide - i.e. it pinned the existence of
    /// the squeeze as much as the remedy, and was satisfied as long as hiding the hint helped. The
    /// owner then ran into the squeeze at a width above the collapse threshold, where nothing was
    /// hidden and the suggestion was ellipsised to <c>doc...</c> anyway.
    /// </para>
    /// <para>
    /// So the invariant is now the floor itself: the content column has a <c>MinWidth</c> that the
    /// chrome cannot take, and the hint is what overflows instead. The old negative control is
    /// deliberately inverted - the two measurements must now be <em>close</em>, because the hint no
    /// longer decides how much of the suggestion the user can read.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void CommandAssistBubbleView_AtTheNarrowBubbleFloor_TheSummaryKeepsItsMinimumWidth()
    {
        const double bubbleWidth = 280;

        // The MinWidth on the content column, less the margin that separates it from the query.
        const double summaryFloor = 150 - 10;

        double withHint = MeasureBubbleSummaryWidth(showShortcutHint: true, bubbleWidth);
        double withoutHint = MeasureBubbleSummaryWidth(showShortcutHint: false, bubbleWidth);

        Assert.True(
            withHint >= summaryFloor,
            $"The suggestion must keep its floor with the hint strip on screen, but had {withHint:F0} px " +
            $"of {bubbleWidth:F0} (floor {summaryFloor:F0}). This is the owner's 'doc...' bubble.");
        Assert.True(
            withoutHint >= summaryFloor,
            $"The suggestion must keep its floor with the hint strip collapsed too, but had " +
            $"{withoutHint:F0} px of {bubbleWidth:F0} (floor {summaryFloor:F0}).");
        Assert.True(
            withHint >= withoutHint * 0.6,
            $"The hint strip must no longer decide how much of the suggestion is readable, but the " +
            $"summary had {withHint:F0} px with it and {withoutHint:F0} px without it. Chrome is " +
            "supposed to overflow before content does.");
    }

    /// <summary>
    /// And the collapse is driven by the same compact-layout decision that already hides the query
    /// echo, from the real placement path on a real narrow pane.
    /// </summary>
    [AvaloniaFact]
    public async Task TerminalPane_WhenTheBubbleIsNarrow_CollapsesTheShortcutHint()
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

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());
        CommandAssistBarViewModel vm = Assert.IsType<CommandAssistBarViewModel>(pane.CommandAssistViewModel);

        Assert.True(layout.UseCompactBubbleLayout);
        Assert.Equal(280, layout.BubbleRect.Width, precision: 1);
        Assert.False(vm.Bubble.ShowShortcutHint);
        Assert.False(vm.Bubble.ShowQueryText);
    }

    /// <summary>
    /// The control: a pane with room keeps the hint, or the assertion above would be satisfied by a hint
    /// that never renders anywhere.
    /// </summary>
    [AvaloniaFact]
    public async Task TerminalPane_WhenTheBubbleHasRoom_KeepsTheShortcutHint()
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

        CommandAssistAnchorLayout layout = Assert.IsType<CommandAssistAnchorLayout>(pane.CalculateCommandAssistAnchorLayoutForTest());
        CommandAssistBarViewModel vm = Assert.IsType<CommandAssistBarViewModel>(pane.CommandAssistViewModel);

        Assert.False(layout.UseCompactBubbleLayout);
        Assert.True(vm.Bubble.ShowShortcutHint);
    }

    /// <summary>
    /// The pass-through property, pinned (PR #290 review): the overlay host paints no background, so a
    /// click anywhere except on a child reaches the terminal underneath, and the bubble - a status
    /// readout with nothing to click, sitting right over the row the user selects text on - opts out of
    /// hit testing individually.
    /// </summary>
    /// <remarks>
    /// The host's own <c>IsHitTestVisible</c> is asserted <em>true</em> deliberately. It was false once,
    /// which excludes the whole subtree: no click could reach a popup row, and a child cannot opt back
    /// into hit testing its parent has switched off. Both halves have to stay as they are.
    /// </remarks>
    [AvaloniaFact]
    public void TerminalPane_OverlayHostPassesClicksThroughAndTheBubbleTakesNone()
    {
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);

        Grid overlayHost = Assert.IsType<Grid>(pane.FindControl<Grid>("CommandAssistOverlayHost"));
        CommandAssistBubbleView bubbleView = Assert.IsType<CommandAssistBubbleView>(pane.FindControl<CommandAssistBubbleView>("CommandAssistBubble"));
        CommandAssistPopupView popupView = Assert.IsType<CommandAssistPopupView>(pane.FindControl<CommandAssistPopupView>("CommandAssistPopup"));

        Assert.Null(overlayHost.Background);
        Assert.True(overlayHost.IsHitTestVisible);
        Assert.False(bubbleView.IsHitTestVisible);
        Assert.True(popupView.IsHitTestVisible);
    }

    /// <summary>
    /// Right- and middle-clicks on the popup are swallowed rather than left to bubble into the pane,
    /// where they would open the pane context menu over the list or paste into the shell beneath it.
    /// </summary>
    [Fact]
    public void CommandAssistPopupView_SwallowsRightAndMiddleClicksOnly()
    {
        Assert.True(CommandAssistPopupView.IsSwallowedPointerButton(isRightButtonPressed: true, isMiddleButtonPressed: false));
        Assert.True(CommandAssistPopupView.IsSwallowedPointerButton(isRightButtonPressed: false, isMiddleButtonPressed: true));
        Assert.False(CommandAssistPopupView.IsSwallowedPointerButton(isRightButtonPressed: false, isMiddleButtonPressed: false));
    }

    /// <summary>
    /// Arranges a bubble at <paramref name="width"/> and returns the arranged width of the summary
    /// TextBlock - the <c>*</c> column's share of it.
    /// </summary>
    private static double MeasureBubbleSummaryWidth(bool showShortcutHint, double width)
    {
        var vm = new CommandAssistBubbleViewModel
        {
            IsVisible = true,
            ModeLabel = "Suggest",
            QueryText = "git st",
            SummaryText = "git status --short --branch",
            ShortcutHintText = CommandAssistBarViewModel.IdleHintText,

            // Compact layout hides both, and the query echo is not what this measures.
            ShowQueryText = false,
            ShowShortcutHint = showShortcutHint
        };
        var view = new CommandAssistBubbleView
        {
            DataContext = vm,
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        // Hosted in a window rather than measured detached: the bubble's content lives behind a
        // ContentPresenter, which only realizes its child during a layout pass from a visual root. A
        // detached Measure/Arrange leaves every child at zero bounds, which would make this pass for the
        // wrong reason.
        var window = new Window
        {
            Content = view,
            Width = width + 120,
            Height = 200
        };
        try
        {
            window.Show();
            RelayoutTo(window, view, new Size(width + 120, 200));

            TextBlock summary = Assert.IsType<TextBlock>(view.FindControl<TextBlock>("BubbleSummaryText"));
            return summary.Bounds.Width;
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CommandAssistPopupView_BindsResultListAndDetailState()
    {
        var suggestions = new ObservableCollection<CommandAssistSuggestionItemViewModel>
        {
            new(
                displayText: "git status",
                descriptionText: "Show working tree state.",
                badgesText: "History",
                metadataText: @"C:\repo",
                isSelected: true,
                type: AssistSuggestionType.History)
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
                displayText: "git status",
                descriptionText: "Show working tree state.",
                badgesText: "History",
                metadataText: @"C:\repo",
                isSelected: true,
                type: AssistSuggestionType.History)
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
    /// Emits an <c>OSC 133;B</c> mark on viewport row <paramref name="row"/>.
    /// </summary>
    /// <remarks>
    /// The mark records wherever the cursor is when B is dispatched, so positioning the cursor is
    /// how a test chooses the marked row. Nothing is written after it: the anchor is the row, not
    /// the text on it, and a test that types would only be testing the write path again.
    /// </remarks>
    private static void MarkPromptAt(TerminalPane pane, int row)
    {
        pane.CreateAndWireParser();
        Assert.NotNull(pane.Buffer);
        Assert.True(row < pane.Buffer!.Rows, $"Row {row} is outside the {pane.Buffer.Rows}-row buffer this pane was arranged to.");
        pane.Buffer.SetCursorPosition(0, row);
        pane.Parser!.Process("\x1b]133;B\x07");
    }

    /// <summary>
    /// Re-runs layout from <paramref name="root"/> down to <paramref name="leaf"/>.
    /// </summary>
    /// <remarks>
    /// <c>Layoutable.InvalidateMeasure</c> does not walk up the tree - it registers the control with
    /// the visual root's layout manager and lets that walk up on the next pass. A test that drives
    /// <c>Measure</c>/<c>Arrange</c> by hand has no such pass, so an invalidated leaf is unreachable:
    /// every still-valid ancestor short-circuits before recursing into it, and the leaf keeps whatever
    /// bounds it had (here, none - the overlay host was collapsed the last time layout ran). Marking
    /// the chain invalid restores the recursion.
    /// </remarks>
    private static void RelayoutTo(Control root, Visual leaf, Size size)
    {
        for (Visual? visual = leaf; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Layoutable layoutable)
            {
                layoutable.InvalidateMeasure();
                layoutable.InvalidateArrange();
            }

            if (ReferenceEquals(visual, root))
            {
                break;
            }
        }

        root.Measure(size);
        root.Arrange(new Rect(size));
    }

    /// <summary>
    /// Makes the bound Command Assist view model visible, the way production does.
    /// </summary>
    /// <remarks>
    /// Visibility is what <c>TerminalPane</c> watches: setting it raises
    /// <c>PropertyChanged</c>, which is the pane's own route into
    /// <c>UpdateCommandAssistOverlayPlacement</c> - so this drives the real placement path rather
    /// than a test seam onto it. <c>OpenCommandAssistHelp</c> is called only because it is the
    /// public entry point that constructs the controller, and therefore the view model there is to
    /// bind; whether help itself finds anything is irrelevant here.
    /// </remarks>
    private static CommandAssistBarViewModel ShowCommandAssist(TerminalPane pane)
    {
        pane.OpenCommandAssistHelp();
        CommandAssistBarViewModel vm = Assert.IsType<CommandAssistBarViewModel>(pane.CommandAssistViewModel);
        vm.IsVisible = true;

        // Help opens the popup, and since the UX-polish round an open popup takes the bubble down -
        // exactly one surface at a time. These are bubble-placement tests, so they close it: the
        // state under test is "a bubble is on screen and has to be put somewhere", which is what a
        // passive Suggest surface looks like. See CommandAssistBarViewModel.SyncPresentationState.
        vm.IsPopupOpen = false;
        Assert.True(vm.Bubble.IsVisible);
        return vm;
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
