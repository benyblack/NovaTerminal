using Avalonia;
using Avalonia.Media;
using System;
using NovaTerminal.Core;
using NovaTerminal.VT;

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
            TerminalLogger.Log("Build: " + DescribeBuild());
            StartupPerformanceTracker.StartNewCurrent();

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

    // Identifies exactly which build is running, so a stale side-by-side copy is obvious
    // in debug.log. Reports git SHA (stamped at compile via the StampGitInfo MSBuild target),
    // the binary path, and its on-disk build time. This is the line that would have
    // immediately flagged the "net10.0 - Copy" stale-binary crash incident.
    private static string DescribeBuild()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();

        string sha = "unknown";
        foreach (var meta in asm.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
        {
            if (meta is System.Reflection.AssemblyMetadataAttribute m && m.Key == "GitSha")
            {
                sha = string.IsNullOrEmpty(m.Value) ? "unknown" : m.Value;
                break;
            }
        }

        // Environment.ProcessPath is correct under both normal and single-file/AOT hosting,
        // whereas Assembly.Location is empty for single-file/AOT.
        string path = Environment.ProcessPath ?? asm.Location;
        string builtAt = "?";
        try
        {
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                builtAt = System.IO.File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
            }
        }
        catch
        {
            // Best-effort diagnostics only — never let build-info logging break startup.
        }

        return $"sha={sha} built={builtAt} path={path}";
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions
            {
                FontFamilyMappings = BundledFontCatalog.CreateFontFamilyMappings(),
                DefaultFamilyName = BundledFontCatalog.DefaultTerminalFontFamily
            })
            .LogToTrace();
}
