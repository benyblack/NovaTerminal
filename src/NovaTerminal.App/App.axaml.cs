using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NovaTerminal.Core;

namespace NovaTerminal;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            StartupPerformanceTracker.Current?.TryMark(StartupPhase.MainWindowConstructed);

            // Enable DevTools for debugging - Press F12 to open
#if DEBUG
            this.AttachDeveloperTools();
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }
}
