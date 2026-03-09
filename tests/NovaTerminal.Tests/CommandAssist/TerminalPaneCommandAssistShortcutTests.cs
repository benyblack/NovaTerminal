using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;

namespace NovaTerminal.Tests.CommandAssist;

public sealed class TerminalPaneCommandAssistShortcutTests
{
    [AvaloniaFact]
    public void TryToggleCommandAssistPinShortcut_WhenAssistVisibleWithoutSelection_ReturnsFalse()
    {
        var pane = new TerminalPane();
        pane.ToggleCommandAssist();

        bool handled = pane.TryToggleCommandAssistPinShortcut();

        Assert.False(handled);
    }
}
