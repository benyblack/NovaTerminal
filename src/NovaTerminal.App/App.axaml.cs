using System;
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
            var tracker = StartupPerformanceTracker.Current
                ?? throw new InvalidOperationException(
                    "StartupPerformanceTracker.StartNewCurrent must run before App init.");

            var services = AppServices.Build(
                tracker,
                schedule: action => Avalonia.Threading.Dispatcher.UIThread.Post(
                    action,
                    Avalonia.Threading.DispatcherPriority.Background));

            desktop.MainWindow = new MainWindow(services);
            services.Startup.Mark(StartupPhase.MainWindowConstructed);

            // Enable DevTools for debugging - Press F12 to open
#if DEBUG
            this.AttachDeveloperTools();
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }
}
