using System.IO;
using System.Linq;
using System.Reflection;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Task 7 fix round 1, finding 1: <c>NovaTerminal.App/Program.cs</c> is a second CLI dispatch
/// table, separate from the dev-only <c>NovaTerminal.Cli/Program.cs</c> shim — and it is the one
/// that matters in a shipped self-contained/AOT build, which has no <c>NovaTerminal.Cli</c>
/// sibling and so must serve every CLI verb itself (see the comment on <c>ReplayCommand</c>'s
/// dispatch in <c>App/Program.cs</c>). <c>BackupCommand</c> shipped wired only into the Cli shim:
/// correct-looking, tested, and unreachable in the build shape the feature actually has to work
/// in — a bare <c>backup export …</c> would fall through every check and launch the GUI.
///
/// This guards against the same mistake recurring for the next CLI command. It cross-checks two
/// "dispatch tables": what CLI-command-shaped types exist (found by reflection over the App
/// assembly for the static <c>IsSupportedCliMode(string[])</c> / <c>Execute(string[], TextWriter,
/// TextWriter, ...)</c> shape every command — <c>SshAskPassCommand</c>, <c>VtReportCommand</c>,
/// <c>ReplayCommand</c>, <c>BackupCommand</c> — already follows) against what is actually
/// dispatched from <c>App/Program.cs</c>'s <c>Main</c>.
///
/// The "is it dispatched" half is a source-text scan rather than more reflection, and that is a
/// deliberate choice, not a shortcut: reflection (or NetArchTest, which is IL/metadata-level too)
/// can enumerate a type's members, but "does <c>Main</c> call this static method" is a question
/// about call sites inside one method body, which needs either IL-instruction inspection of
/// <c>Main</c> (fragile — the dispatcher is a plain if-chain, not a data structure anything can
/// enumerate) or reading the source. A loose <c>"TypeName.IsSupportedCliMode("</c> substring
/// match is enough to catch the actual failure mode (the type is nowhere in the file) without the
/// cost of a full Roslyn parse; a false negative would need a decoy string that names a real
/// command type while dispatching nothing, which is not a realistic accident.
/// </summary>
public class CliCommandDispatchTests
{
    // The App assembly is named "NovaTerminal" (see LayeringTests' CommandAssist comment and
    // NovaTerminal.App.csproj's <AssemblyName>).
    private static Assembly App => Assembly.Load("NovaTerminal");

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NovaTerminal.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private const BindingFlags CommandMemberFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

    // Hoisted out of HasCliCommandShape (called once per type in the assembly) to satisfy
    // CA1861 — this project builds with TreatWarningsAsErrors.
    private static readonly Type[] StringArrayParameter = [typeof(string[])];

    /// <summary>
    /// A type has the CLI-command shape when it exposes both a static
    /// <c>bool IsSupportedCliMode(string[])</c> and a static <c>Execute</c> method whose first
    /// three parameters are exactly <c>(string[], TextWriter, TextWriter)</c> — the pattern every
    /// existing command follows. Trailing parameters (e.g. <c>BackupCommand</c>'s
    /// <c>rootOverride</c> test seam) are allowed only if optional, since every dispatch site
    /// calls the bare three-argument form.
    /// </summary>
    private static Type[] DiscoverCliCommandTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types in a large Avalonia app assembly can fail to load in a reflection-only
            // enumeration context; the ones that did load are still meaningful to scan.
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        return types.Where(HasCliCommandShape).ToArray();
    }

    private static bool HasCliCommandShape(Type type)
    {
        var isSupported = type.GetMethod("IsSupportedCliMode", CommandMemberFlags, StringArrayParameter);
        if (isSupported is null || isSupported.ReturnType != typeof(bool)) return false;

        return type.GetMethods(CommandMemberFlags).Any(m => m.Name == "Execute" && HasCliExecuteSignature(m));
    }

    private static bool HasCliExecuteSignature(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return parameters.Length >= 3
            && parameters[0].ParameterType == typeof(string[])
            && parameters[1].ParameterType == typeof(TextWriter)
            && parameters[2].ParameterType == typeof(TextWriter)
            && parameters.Skip(3).All(p => p.IsOptional);
    }

    [Fact]
    public void Every_CLI_command_type_is_dispatched_from_the_App_entry_point()
    {
        var commandTypes = DiscoverCliCommandTypes(App);

        // Pins that discovery itself still finds the known commands. Without this, a change to
        // the shared shape (e.g. every command renamed off Execute/IsSupportedCliMode) would
        // silently shrink "table A" to nothing and this test would pass vacuously — asserting
        // zero offenders among zero discovered commands proves nothing.
        Assert.Contains(commandTypes, t => t.Name == "BackupCommand");
        Assert.Contains(commandTypes, t => t.Name == "ReplayCommand");
        Assert.Contains(commandTypes, t => t.Name == "SshAskPassCommand");
        Assert.Contains(commandTypes, t => t.Name == "VtReportCommand");

        string programSource = File.ReadAllText(Path.Combine(RepoRoot(), "src/NovaTerminal.App/Program.cs"));

        var undispatched = commandTypes
            .Where(t => !programSource.Contains(t.Name + ".IsSupportedCliMode(", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.True(undispatched.Length == 0,
            "These CLI-command-shaped types exist but are not dispatched from " +
            "src/NovaTerminal.App/Program.cs — the entry point a shipped self-contained/AOT build " +
            "actually runs (NovaTerminal.Cli/Program.cs is a dev-only shim absent from that " +
            "bundle, so wiring a command into it alone leaves the command unreachable there). Add " +
            "an IsSupportedCliMode/Execute branch to App/Program.cs's Main, following the " +
            $"ReplayCommand precedent. Offenders: {string.Join(", ", undispatched)}");
    }
}
