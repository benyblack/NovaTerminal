using System.Xml.Linq;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Guards every shipped .axaml against runtime (reflection) bindings.
/// </summary>
/// <remarks>
/// Releases publish with NativeAOT (see <c>publish_aot</c> in <c>.github/workflows/release.yml</c>,
/// and <c>PublishAot</c> in <c>NovaTerminal.App.csproj</c>). A reflection binding -
/// <c>{Binding SomePath}</c> evaluated at runtime rather than compiled - resolves its path with
/// <c>Type.GetProperty</c>, and ILC has already trimmed away any property getter no compiled code
/// calls. The binding then silently produces nothing: no exception, no visible error, just a
/// control that renders blank in the installed build while being perfectly fine in every dev build
/// and every test, because those run JIT with full metadata.
///
/// That is not hypothetical. The command palette's item template carried
/// <c>x:CompileBindings="False"</c> and bound <c>{Binding FullTitle}</c> / <c>{Binding Shortcut}</c>.
/// In released Windows builds the palette opened, filtered, and executed on click - all plain C# -
/// but every row's text was invisible, because those two getters were the only readers of
/// <c>TerminalCommand.FullTitle</c> in the whole program and so were not compiled at all.
///
/// ILC does warn (IL2026 + IL3050, pointing at the exact TextBlock lines), but only during
/// <c>dotnet publish -p:PublishAot=true</c> - not on a normal build, and not as an error. The
/// warning shipped in release logs for as long as the bug did. This test moves the signal to where
/// it gets read: a plain unit test that fails on any ordinary <c>dotnet test</c> run.
///
/// The fix for a violation is never to suppress this test. It is to give the template an
/// <c>x:DataType</c> so the binding compiles - see <c>TransferCenter.axaml</c>'s
/// <c>&lt;DataTemplate x:DataType="core:TransferJob"&gt;</c> for the shape.
/// </remarks>
public class CompiledBindingTests
{
    // Same walk-up as ProjectFileLayeringTests.RepoRoot(); the test binary sits several levels
    // below the repo root and the depth differs between a local run and CI.
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

    private static IEnumerable<(string RelativePath, int LineNumber, string Text)> ShippedXamlLines()
    {
        var root = RepoRoot();
        var srcDir = Path.Combine(root, "src");

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.axaml", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

            // obj/ holds generated copies of the very files being scanned, which would report each
            // offender twice under a path nobody edits.
            if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                yield return (relative, i + 1, lines[i]);
            }
        }
    }

    /// <summary>
    /// <c>AvaloniaUseCompiledBindingsByDefault</c> is what makes a missing <c>x:DataType</c> a build
    /// error instead of a silent downgrade to reflection, so every other guard here rests on it.
    /// Without it, a new <c>{Binding SomePath}</c> anywhere in the app would compile clean and fail
    /// only once installed - and it would carry none of the <c>CompileBindings="False"</c> spelling
    /// the test below looks for.
    /// </summary>
    [Fact]
    public void App_must_compile_bindings_by_default()
    {
        var csproj = XDocument.Load(Path.Combine(RepoRoot(), "src", "NovaTerminal.App", "NovaTerminal.App.csproj"));
        var value = csproj.Descendants("AvaloniaUseCompiledBindingsByDefault").Select(e => e.Value).FirstOrDefault();

        Assert.True(
            string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            "NovaTerminal.App.csproj must set <AvaloniaUseCompiledBindingsByDefault>true</...>. It is " +
            "the only thing that turns a forgotten x:DataType into a build error rather than a binding " +
            "that resolves by reflection, works in every dev build and test, and renders blank in the " +
            $"NativeAOT release. Found: {value ?? "(property absent)"}.");
    }

    [Fact]
    public void No_shipped_axaml_opts_out_of_compiled_bindings()
    {
        var offenders = ShippedXamlLines()
            .Where(l => l.Text.Contains("CompileBindings=\"False\"", StringComparison.OrdinalIgnoreCase))
            .Select(l => $"{l.RelativePath}:{l.LineNumber}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "x:CompileBindings=\"False\" turns every {Binding Path} in that scope into a reflection " +
            "binding, which the NativeAOT release build cannot resolve: the bound properties are " +
            "trimmed, the binding fails silently, and the control renders blank in the installed app " +
            "while looking correct in every dev build and test. Give the template an x:DataType " +
            "instead (see TransferCenter.axaml). Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The explicit <c>{ReflectionBinding ...}</c> markup extension is the other spelling of the same
    /// hazard, and it needs no <c>CompileBindings</c> attribute to appear - so the test above would
    /// not see it.
    /// </summary>
    [Fact]
    public void No_shipped_axaml_uses_the_ReflectionBinding_markup_extension()
    {
        var offenders = ShippedXamlLines()
            .Where(l => l.Text.Contains("ReflectionBinding", StringComparison.Ordinal))
            .Select(l => $"{l.RelativePath}:{l.LineNumber}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "{ReflectionBinding} resolves its path with Type.GetProperty at runtime, which the " +
            "NativeAOT release build has already trimmed - the binding then renders blank in the " +
            "installed app and nowhere else. Use a compiled binding with x:DataType. Offenders: " +
            string.Join(", ", offenders));
    }
}
