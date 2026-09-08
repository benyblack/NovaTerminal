using System.Xml;
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

    private static List<string> ShippedXamlFiles()
    {
        var root = RepoRoot();
        var files = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.axaml", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

            // obj/ and bin/ hold generated copies of the very files being scanned, which would
            // report each offender twice under a path nobody edits.
            if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            files.Add(relative);
        }

        return files;
    }

    /// <summary>
    /// Reads the XAML as XML and inspects attributes, rather than searching the raw text for a
    /// spelling.
    /// </summary>
    /// <remarks>
    /// The first version of this guard matched the literal string <c>CompileBindings="False"</c>,
    /// and a review caught that it was blind to <c>x:CompileBindings='False'</c> (single quotes are
    /// valid <c>AttValue</c>), to <c>x:CompileBindings = "False"</c> (XML's <c>Eq</c> production is
    /// <c>S? '=' S?</c>), and to an attribute wrapped across two lines - each of which is accepted
    /// by the XAML compiler and would have let the exact regression this file exists to prevent
    /// back in under a green test.
    ///
    /// That is the same mistake, in the same direction, that <c>scripts/build.sh</c> records twice
    /// in its own comments: an allowlist of a hazard's known spellings catches only the known
    /// spellings. So this reads the document the way the XAML compiler does and asks about
    /// attribute identity and value, where the spelling of the source text cannot matter.
    ///
    /// Deliberately matched on <c>LocalName</c> alone, ignoring which prefix is bound to the XAML
    /// directive namespace: the <c>x</c> in <c>x:CompileBindings</c> is just this repo's convention,
    /// and a file declaring the namespace under any other prefix means exactly the same thing to
    /// the compiler. The cost of the looser match is a loud, one-line failure on some unrelated
    /// attribute that happens to share the name; the cost of the tighter one is a silent blank
    /// control in a shipped release. Any value other than <c>true</c> counts as an opt-out for the
    /// same reason - fail closed.
    /// </remarks>
    private static List<string> FindReflectionBindings(string relativePath, string xaml)
    {
        var findings = new List<string>();
        var document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);

        foreach (var element in document.Descendants())
        {
            if (string.Equals(element.Name.LocalName, "ReflectionBinding", StringComparison.Ordinal))
            {
                findings.Add(Describe(relativePath, element, "<ReflectionBinding> element syntax"));
            }

            foreach (var attribute in element.Attributes())
            {
                if (string.Equals(attribute.Name.LocalName, "CompileBindings", StringComparison.Ordinal) &&
                    !string.Equals(attribute.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(Describe(relativePath, attribute, $"CompileBindings is \"{attribute.Value.Trim()}\""));
                }

                if (attribute.Value.Contains("ReflectionBinding", StringComparison.Ordinal))
                {
                    findings.Add(Describe(relativePath, attribute, $"{attribute.Name.LocalName} uses the ReflectionBinding markup extension"));
                }
            }
        }

        return findings;
    }

    private static string Describe(string relativePath, XObject node, string what)
    {
        var lineInfo = (IXmlLineInfo)node;
        var where = lineInfo.HasLineInfo() ? $"{relativePath}:{lineInfo.LineNumber}" : relativePath;
        return $"{where} ({what})";
    }

    private const string Remedy =
        "A reflection binding resolves its path with Type.GetProperty at runtime, and the NativeAOT " +
        "release build has already trimmed the bound property away: the binding fails silently and " +
        "the control renders blank in the installed app while looking correct in every dev build and " +
        "every test. Give the template an x:DataType so the binding compiles - see " +
        "TransferCenter.axaml for the shape - and never suppress this test. Offenders: ";

    /// <summary>
    /// <c>AvaloniaUseCompiledBindingsByDefault</c> is what makes a missing <c>x:DataType</c> a build
    /// error instead of a silent downgrade to reflection, so every other guard here rests on it.
    /// Without it, a new <c>{Binding SomePath}</c> anywhere in the app would compile clean and fail
    /// only once installed - and it would carry neither a <c>CompileBindings</c> attribute nor a
    /// <c>ReflectionBinding</c> spelling for <see cref="FindReflectionBindings"/> to find.
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
    public void No_shipped_axaml_uses_reflection_bindings()
    {
        var root = RepoRoot();
        var files = ShippedXamlFiles();

        // A file this scan cannot read is not a pass. Anything the XAML compiler accepts parses as
        // XML, so a parse failure here means the scan skipped a file it was supposed to cover.
        Assert.NotEmpty(files);

        var offenders = files
            .SelectMany(relative => FindReflectionBindings(relative, File.ReadAllText(Path.Combine(root, relative))))
            .ToArray();

        Assert.True(offenders.Length == 0, Remedy + string.Join(", ", offenders));
    }

    /// <summary>
    /// Proves the scan is spelling-independent, over the exact forms the first version of this guard
    /// let through. Each case is valid XML that the XAML compiler accepts and that means "opt out of
    /// compiled bindings"; every one of them must be caught.
    /// </summary>
    [Theory]
    [InlineData("double quotes", """x:CompileBindings="False" """)]
    [InlineData("single quotes", """x:CompileBindings='False' """)]
    [InlineData("spaces around the equals sign", """x:CompileBindings = "False" """)]
    [InlineData("lowercase value", """x:CompileBindings="false" """)]
    [InlineData("wrapped across lines", "x:CompileBindings=\n            \"False\"")]
    [InlineData("value padded with whitespace", """x:CompileBindings=" False " """)]
    public void Opt_out_is_detected_however_it_is_spelled(string description, string attribute)
    {
        var findings = FindReflectionBindings("synthetic.axaml", Template(attribute));

        Assert.True(findings.Count > 0, $"The scan missed a CompileBindings opt-out written with {description}.");
    }

    /// <summary>
    /// The <c>x</c> prefix is this repo's convention, not part of the language: a file is free to
    /// bind the XAML directive namespace to any prefix, and the directive still means the same thing.
    /// </summary>
    [Fact]
    public void Opt_out_is_detected_under_a_different_namespace_prefix()
    {
        var xaml = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:xaml="http://schemas.microsoft.com/winfx/2006/xaml">
              <DataTemplate xaml:CompileBindings="False" />
            </Window>
            """;

        Assert.NotEmpty(FindReflectionBindings("synthetic.axaml", xaml));
    }

    [Theory]
    [InlineData("markup extension", """Text="{ReflectionBinding FullTitle}" """)]
    [InlineData("markup extension with a converter", """Text="{ReflectionBinding FullTitle, Converter={x:Static Foo.Bar}}" """)]
    public void ReflectionBinding_markup_extension_is_detected(string description, string attribute)
    {
        var findings = FindReflectionBindings("synthetic.axaml", Template(attribute));

        Assert.True(findings.Count > 0, $"The scan missed a ReflectionBinding written as a {description}.");
    }

    [Fact]
    public void ReflectionBinding_element_syntax_is_detected()
    {
        var xaml = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <TextBlock>
                <TextBlock.Text>
                  <ReflectionBinding Path="FullTitle" />
                </TextBlock.Text>
              </TextBlock>
            </Window>
            """;

        Assert.NotEmpty(FindReflectionBindings("synthetic.axaml", xaml));
    }

    /// <summary>
    /// The other half of a guard's correctness: it must stay quiet on the shapes this codebase is
    /// supposed to use, or it becomes noise someone silences.
    /// </summary>
    [Theory]
    [InlineData("an explicit opt-in", """x:CompileBindings="True" """)]
    [InlineData("a lowercase opt-in", """x:CompileBindings="true" """)]
    [InlineData("a compiled binding with a data type", """x:DataType="shell:TerminalCommand" """)]
    [InlineData("a plain compiled binding", """Tag="{Binding FullTitle}" """)]
    public void Compiled_binding_shapes_are_not_flagged(string description, string attribute)
    {
        var findings = FindReflectionBindings("synthetic.axaml", Template(attribute));

        Assert.True(findings.Count == 0, $"The scan wrongly flagged {description}: {string.Join(", ", findings)}");
    }

    private static string Template(string attribute) =>
        $"""
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <DataTemplate {attribute}/>
        </Window>
        """;
}
