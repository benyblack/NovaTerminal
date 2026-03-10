using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NovaTerminal.Controls;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class TerminalPaneCommandAssistShortcutTests
{
    [AvaloniaFact]
    public void TryToggleCommandAssistPinShortcut_WhenAssistVisibleWithoutSelection_ReturnsFalse()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.ToggleCommandAssist();

        bool handled = pane.TryToggleCommandAssistPinShortcut();

        Assert.False(handled);
    }

    [AvaloniaFact]
    public async Task OpenCommandAssistHelp_WhenQueryPresent_UsesPaneInfrastructure()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("Get-ChildItem");

        bool handled = pane.OpenCommandAssistHelp();
        await Task.Delay(50);

        CommandAssistBarViewModel vm = AssertViewModel(pane);
        Assert.True(handled);
        Assert.True(vm.IsVisible);
        Assert.Equal("Help", vm.ModeLabel);
        Assert.True(vm.HasSuggestions);
    }

    [AvaloniaFact]
    public async Task HandleCommandAssistCompletionAsync_WhenNonZeroExit_OpensFixModeForTrackedCommand()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("gti status");

        await pane.HandleCommandAssistCompletionAsync(127);
        await Task.Delay(50);

        CommandAssistBarViewModel vm = AssertViewModel(pane);
        Assert.True(vm.IsVisible);
        Assert.Equal("Fix", vm.ModeLabel);
    }

    [AvaloniaFact]
    public async Task HandleCommandAssistCompletionAsync_WhenZeroExit_DoesNotOpenFixMode()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("gti status");

        await pane.HandleCommandAssistCompletionAsync(0);
        await Task.Delay(50);

        CommandAssistBarViewModel vm = AssertViewModel(pane);
        Assert.NotEqual("Fix", vm.ModeLabel);
    }

    [AvaloniaFact]
    public async Task HandleCommandAssistCompletionAsync_WhenKnownCommandFails_DoesNotOpenTypoFixMode()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("git commit");

        await pane.HandleCommandAssistCompletionAsync(1);
        await Task.Delay(50);

        CommandAssistBarViewModel vm = AssertViewModel(pane);
        Assert.NotEqual("Fix", vm.ModeLabel);
        Assert.False(vm.IsVisible && vm.ShowEmptyState);
    }

    [AvaloniaFact]
    public void CanExplainSelection_WhenSelectionIsEmpty_ReturnsFalse()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);

        Assert.False(pane.CanExplainSelection());
    }

    [AvaloniaFact]
    public async Task ExplainSelectionAsync_WhenSelectionTextProvided_OpensHelp()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);

        bool canExplain = pane.CanExplainSelection("fatal: not a git repository");
        bool opened = await pane.ExplainSelectionAsync("fatal: not a git repository");
        await Task.Delay(50);

        CommandAssistBarViewModel vm = AssertViewModel(pane);
        Assert.True(canExplain);
        Assert.True(opened);
        Assert.Equal("Help", vm.ModeLabel);
    }

    [AvaloniaFact]
    public void TryHandleCommandAssistHelpShortcut_WhenPaneOpensHelp_ReturnsTrue()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("git checkout");

        bool handled = NovaTerminal.MainWindow.TryHandleCommandAssistHelpShortcut(
            pane,
            Key.H,
            KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(handled);
    }

    [AvaloniaFact]
    public void TryHandleCommandAssistHelpShortcut_WhenDifferentShortcut_ReturnsFalse()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);

        bool handled = NovaTerminal.MainWindow.TryHandleCommandAssistHelpShortcut(
            pane,
            Key.P,
            KeyModifiers.Control | KeyModifiers.Shift);

        Assert.False(handled);
    }

    private static CommandAssistBarViewModel AssertViewModel(TerminalPane pane)
    {
        var commandAssistBar = pane.FindControl<Control>("CommandAssistBar");
        Assert.NotNull(commandAssistBar);
        return Assert.IsType<CommandAssistBarViewModel>(commandAssistBar.DataContext);
    }

    private static void ConfigureCommandAssist(TerminalPane pane)
    {
        var settings = TerminalSettings.Load();
        settings.CommandAssistEnabled = true;
        settings.CommandAssistHistoryEnabled = true;
        pane.ApplySettings(settings);
    }
}
