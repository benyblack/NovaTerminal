using NovaTerminal.Shell;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using NovaTerminal.Controls;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.Platform;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class TerminalPaneCommandAssistShortcutTests
{
    [AvaloniaFact]
    public void ApplySettings_WhenAssistEnabled_DoesNotEagerlyInitializeController()
    {
        using var pane = new TerminalPane();
        pane.CommandAssistServices = TestCommandAssistServices.Instance;
        var settings = new TerminalSettings(); // constructed, not Load() - see #232
        settings.CommandAssistEnabled = true;
        settings.CommandAssistHistoryEnabled = true;

        pane.ApplySettings(settings);

        Assert.Null(pane.CommandAssistViewModel);
    }

    [AvaloniaFact]
    public void TryToggleCommandAssistPinShortcut_WhenAssistVisibleWithoutSelection_ReturnsFalse()
    {
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.ToggleCommandAssist();

        bool handled = pane.TryToggleCommandAssistPinShortcut();

        Assert.False(handled);
    }

    [AvaloniaFact]
    public void TryHandleCommandAssistKey_WhenAssistVisible_DoesNotConsumeTab()
    {
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.ToggleCommandAssist();

        bool handled = pane.TryHandleCommandAssistKey(Key.Tab, KeyModifiers.None);

        Assert.False(handled);
    }

    [AvaloniaFact]
    public void OpenCommandAssistHelp_WhenDisabledInSettings_ReturnsFalse()
    {
        using var pane = new TerminalPane();
        var settings = new TerminalSettings(); // constructed, not Load() - see #232
        settings.CommandAssistEnabled = false;
        settings.CommandAssistHistoryEnabled = true;
        pane.ApplySettings(settings);

        bool handled = pane.OpenCommandAssistHelp();

        Assert.False(handled);
        Assert.False(pane.CommandAssistViewModel?.IsVisible ?? false);
    }

    /// <summary>
    /// Phase 1c: the pane's query comes off the grid, so this drives the whole real seam - the
    /// parser sees <c>OSC 133;B</c>, the pane keeps the mark, the controller's lifecycle gate opens,
    /// and Help resolves its command token by reading the cells between the mark and the cursor.
    /// The paste that used to seed a shadow buffer here no longer has anything to seed.
    /// </summary>
    [AvaloniaFact]
    public async Task OpenCommandAssistHelp_WhenTheGridHoldsACommand_UsesPaneInfrastructure()
    {
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.ArmShellIntegrationTracker();
        pane.CreateAndWireParser();
        await TypeAtAnIntegratedPromptAsync(pane, "Get-ChildItem");

        bool handled = pane.OpenCommandAssistHelp();
        await Task.Delay(50);

        CommandAssistBarViewModel vm = AssertViewModel(pane);
        Assert.True(handled);
        Assert.True(vm.IsVisible);
        Assert.Equal("Help", vm.ModeLabel);
        Assert.True(vm.HasSuggestions);
    }

    /// <summary>
    /// The gate at pane level: outside the <c>B</c>..<c>C</c> window the grid still holds the text,
    /// and Help still gets nothing from it. Without the gate the same bytes would produce a help
    /// lookup for whatever the command printed.
    /// </summary>
    [AvaloniaFact]
    public async Task OpenCommandAssistHelp_AfterTheCommandWasSubmitted_TakesNoTokenFromTheGrid()
    {
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.ArmShellIntegrationTracker();
        pane.CreateAndWireParser();
        await TypeAtAnIntegratedPromptAsync(pane, "Get-ChildItem");

        await SubmitAsync(pane, "Get-ChildItem");

        pane.OpenCommandAssistHelp();
        await Task.Delay(50);

        CommandAssistBarViewModel vm = AssertViewModel(pane);
        Assert.Equal(string.Empty, vm.QueryText);
        Assert.False(vm.HasSuggestions);
    }

    /// <summary>
    /// Drives a real integrated prompt: <c>OSC 133;A</c>, prompt text, <c>OSC 133;B</c>, then the
    /// command line itself. The delay lets the pane's serialized shell-integration dispatcher
    /// deliver <c>B</c> to the controller, which is what opens the lifecycle gate.
    /// </summary>
    private static async Task TypeAtAnIntegratedPromptAsync(TerminalPane pane, string commandLine)
    {
        pane.Parser!.Process("\x1b]133;A\x07PS C:\\> \x1b]133;B\x07" + commandLine);
        await Task.Delay(50);
    }

    /// <summary>
    /// <c>OSC 133;C;&lt;base64&gt;</c>, the way all four bootstraps emit it. The payload matters:
    /// the parser only raises <c>OnCommandAccepted</c> for a C that decodes to something, so a
    /// bare <c>133;C</c> would not close the lifecycle gate here.
    /// </summary>
    private static async Task SubmitAsync(TerminalPane pane, string commandLine)
    {
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(commandLine));
        pane.Parser!.Process($"\x1b]133;C;{encoded}\x07");
        await Task.Delay(50);
    }

    [AvaloniaFact]
    public async Task HandleCommandAssistCompletionAsync_WhenNonZeroExit_OpensFixModeForTrackedCommand()
    {
        using var pane = new TerminalPane();
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
        using var pane = new TerminalPane();
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
        using var pane = new TerminalPane();
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
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);

        Assert.False(pane.CanExplainSelection());
    }

    [AvaloniaFact]
    public async Task ExplainSelectionAsync_WhenSelectionTextProvided_OpensHelp()
    {
        using var pane = new TerminalPane();
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
    public void TryOpenCommandAssistHelp_WhenPaneOpensHelp_ReturnsTrue()
    {
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("git checkout");

        bool handled = NovaTerminal.MainWindow.TryOpenCommandAssistHelp(pane);

        Assert.True(handled);
    }

    [AvaloniaFact]
    public void TryOpenCommandAssistHelp_WhenPaneIsMissing_ReturnsFalse()
    {
        bool handled = NovaTerminal.MainWindow.TryOpenCommandAssistHelp(null);

        Assert.False(handled);
    }

    private static CommandAssistBarViewModel AssertViewModel(TerminalPane pane)
    {
        return Assert.IsType<CommandAssistBarViewModel>(pane.CommandAssistViewModel);
    }

    private static void ConfigureCommandAssist(TerminalPane pane)
    {
        // Phase 0b: the pane no longer reaches for a static locator, so the services instance is
        // injected the same way MainWindow injects it in production.
        pane.CommandAssistServices = TestCommandAssistServices.Instance;
        var settings = new TerminalSettings(); // constructed, not Load() - see #232
        settings.CommandAssistEnabled = true;
        settings.CommandAssistHistoryEnabled = true;
        pane.ApplySettings(settings);
    }
}
