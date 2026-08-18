using NovaTerminal.Shell;
using Avalonia;
using Avalonia.Media;
using System;
using NovaTerminal.Platform;
using NovaTerminal.Pty;
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
        // Velopack hook. Setup.exe/Update.exe re-invoke this same binary with --veloapp-*
        // arguments to run install/uninstall/update lifecycle work; Run() handles those and
        // exits the process. It must therefore precede everything -- the CLI-mode checks
        // below, Avalonia, and the try block -- or a hook invocation would be misread as a
        // normal launch. On a normal launch it is a cheap no-op and returns.
        Velopack.VelopackApp.Build().Run();

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

            // Headless replay (A4) — the self-contained AOT bundle ships no separate
            // NovaTerminal.Cli, so the app executable serves `--replay <file>` itself.
            // Rooting ReplayCommand here also keeps AOT trimming from dropping it.
            if (ReplayCommand.IsSupportedCliMode(args))
            {
                CliConsoleBindings.Prepare();
                Environment.ExitCode = ReplayCommand.Execute(args, Console.Out, Console.Error);
                return;
            }

            // The PTY layer cannot reference VT (Pty_must_not_depend_on_Vt), so it reports through its
            // own sink; bridge it here so its diagnostics reach the same debug log as everything else.
            // Before #109 they went to Console.WriteLine, i.e. nowhere in a GUI process.
            PtyLogger.Sink = static (level, message) => TerminalLogger.Log(ToLogLevel(level), message);

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

    /// <summary>
    /// Maps the PTY layer's severity onto the app's.
    /// </summary>
    /// <remarks>
    /// Written out rather than cast. The two enums happen to agree member-for-member today, so
    /// <c>(LogLevel)level</c> would work and would keep working right up until someone inserted a member
    /// into one of them, at which point every PTY message would be silently mislevelled.
    /// <c>PtyLogLevelsMatchAppLogLevels</c> in the architecture tests pins the correspondence.
    /// </remarks>
    internal static LogLevel ToLogLevel(PtyLogLevel level) => level switch
    {
        PtyLogLevel.Debug => LogLevel.Debug,
        PtyLogLevel.Info => LogLevel.Info,
        PtyLogLevel.Warning => LogLevel.Warning,
        PtyLogLevel.Error => LogLevel.Error,
        _ => LogLevel.Info,
    };

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
