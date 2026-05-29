using System.Reflection;
using NetArchTest.Rules;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Each production assembly puts its types in a namespace that matches its assembly name,
/// and no two assemblies share a namespace prefix. The App assembly is the composition
/// root: it owns the bare "NovaTerminal" root plus app-specific buckets (Shell, Controls,
/// Services, Models, ViewModels, Views, UI, CommandAssist) and must not reach into a leaf
/// assembly's reserved prefix.
/// </summary>
public class NamespaceAlignmentTests
{
    private static Assembly LoadByName(string name) => Assembly.Load(name);

    // Leaf assemblies, each owning exactly "NovaTerminal.<Name>.*".
    private static readonly string[] LeafAssemblies =
        { "NovaTerminal.VT", "NovaTerminal.Replay", "NovaTerminal.Rendering",
          "NovaTerminal.Pty", "NovaTerminal.Platform" };

    [Theory]
    [InlineData("NovaTerminal.VT")]
    [InlineData("NovaTerminal.Replay")]
    [InlineData("NovaTerminal.Rendering")]
    [InlineData("NovaTerminal.Pty")]
    [InlineData("NovaTerminal.Platform")]
    public void Leaf_assembly_types_reside_in_its_own_namespace(string asmName)
    {
        var result = Types.InAssembly(LoadByName(asmName))
            .That()
            .DoNotResideInNamespace("System.Runtime.CompilerServices")
            .And().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith(asmName)
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"{asmName} types not in {asmName}.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void No_two_assemblies_share_a_namespace_prefix()
    {
        // Each leaf's reserved prefix must be used by no other assembly (leaf or App).
        // The App project emits assembly name "NovaTerminal" (not "NovaTerminal.App").
        var others = new List<(string Label, string AsmName)>(
            LeafAssemblies.Select(n => (n, n)))
        {
            ("NovaTerminal.App", "NovaTerminal")
        };

        foreach (var owner in LeafAssemblies)
        {
            foreach (var (label, asmName) in others)
            {
                if (label == owner) continue;

                var result = Types.InAssembly(LoadByName(asmName))
                    .That().ArePublic()
                    .Should()
                    .NotResideInNamespaceStartingWith(owner)
                    .GetResult();

                Assert.True(result.IsSuccessful,
                    $"{label} must not use the {owner} namespace prefix. " +
                    $"Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
            }
        }
    }
}
