using Avalonia;
using System;
using NovaTerminal.Core;

namespace NovaTerminal;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // Log startup info
            TerminalLogger.Log("NovaTerminal started with args: " + string.Join(" ", args));
            TerminalLogger.Log("Log file path: " + TerminalLogger.GetLogFilePath());
            
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            TerminalLogger.Log("Startup error: " + ex.ToString());
            System.IO.File.WriteAllText("startup_error.txt", ex.ToString());
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
