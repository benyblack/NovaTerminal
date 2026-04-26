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
            if (VtReportCommand.IsSupportedCliMode(args))
            {
                CliConsoleBindings.Prepare();
                Environment.ExitCode = VtReportCommand.Execute(args, Console.Out, Console.Error);
                return;
            }

            if (SshAskPassCommand.IsSupportedCliMode(args))
            {
                CliConsoleBindings.Prepare();
                Environment.ExitCode = SshAskPassCommand.Execute(args, Console.Out, Console.Error);
                return;
            }

            // Log startup info
            TerminalLogger.Log("NovaTerminal started with args: " + string.Join(" ", args));
            TerminalLogger.Log("Log file path: " + AppLogger.GetLogFilePath());

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            TerminalLogger.Log("Startup error: " + ex.ToString());
            AppPaths.EnsureInitialized();
            System.IO.File.WriteAllText(AppPaths.StartupErrorFilePath, ex.ToString());
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
