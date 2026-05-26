using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

internal static class TestMainWindowFactory
{
    public static NovaTerminal.MainWindow Create()
        => new NovaTerminal.MainWindow(AppServices.BuildForDesigner());
}
