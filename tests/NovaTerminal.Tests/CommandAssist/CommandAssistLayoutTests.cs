using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.CommandAssist.Views;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistLayoutTests
{
    [AvaloniaFact]
    public void CommandAssistBar_IsHostedInTerminalOverlayRow()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        var commandAssistBar = pane.FindControl<CommandAssistBarView>("CommandAssistBar");

        Assert.NotNull(commandAssistBar);
        Assert.Equal(0, Grid.GetRow(commandAssistBar));
    }

    [AvaloniaFact]
    public async Task CommandAssistBar_WhenHelpModeIsOpen_RendersModeAndDescriptionFields()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("Get-ChildItem");
        pane.OpenCommandAssistHelp();
        await Task.Delay(50);

        var commandAssistBar = pane.FindControl<CommandAssistBarView>("CommandAssistBar");
        Assert.NotNull(commandAssistBar);
        var vm = Assert.IsType<CommandAssistBarViewModel>(commandAssistBar.DataContext);

        var modeLabel = commandAssistBar.FindControl<TextBlock>("ModeLabelText");
        var description = commandAssistBar.FindControl<TextBlock>("SelectedDescriptionTextBlock");

        Assert.NotNull(modeLabel);
        Assert.NotNull(description);
        Assert.Equal("Help", modeLabel.Text);
        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedDescriptionText));
    }

    [AvaloniaFact]
    public async Task CommandAssistBar_WhenHelperModeHasNoSuggestions_ShowsEmptyState()
    {
        var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.NotifyCommandAssistPaste("frobnicate");
        pane.OpenCommandAssistHelp();
        await Task.Delay(50);

        CommandAssistBarViewModel vm = Assert.IsType<CommandAssistBarViewModel>(pane.FindControl<CommandAssistBarView>("CommandAssistBar")!.DataContext);
        var emptyState = pane.FindControl<CommandAssistBarView>("CommandAssistBar")!.FindControl<TextBlock>("EmptyStateTextBlock");

        Assert.True(vm.ShowEmptyState);
        Assert.Equal("No local help found.", vm.EmptyStateText);
        Assert.NotNull(emptyState);
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
