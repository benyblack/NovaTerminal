using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.CommandAssist.Views;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class CommandAssistLayoutTests
{
    [AvaloniaFact]
    public void CommandAssistBar_IsHostedInTerminalOverlayRow()
    {
        var pane = new TerminalPane();
        var commandAssistBar = pane.FindControl<CommandAssistBarView>("CommandAssistBar");

        Assert.NotNull(commandAssistBar);
        Assert.Equal(0, Grid.GetRow(commandAssistBar));
    }
}
