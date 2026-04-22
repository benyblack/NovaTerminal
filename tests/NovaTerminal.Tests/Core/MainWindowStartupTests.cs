using Avalonia.Headless.XUnit;

namespace NovaTerminal.Tests.Core;

public sealed class MainWindowStartupTests
{
    [AvaloniaFact]
    public void MainWindow_CanBeConstructed()
    {
        var window = new NovaTerminal.MainWindow();

        Assert.NotNull(window);
    }
}
