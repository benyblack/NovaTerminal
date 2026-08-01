using System.Reflection;
using System.Runtime.CompilerServices;
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
          "NovaTerminal.Pty", "NovaTerminal.Platform", "NovaTerminal.AgentHost.Contracts" };

    [Theory]
    [InlineData("NovaTerminal.VT")]
    [InlineData("NovaTerminal.Replay")]
    [InlineData("NovaTerminal.Rendering")]
    [InlineData("NovaTerminal.Pty")]
    [InlineData("NovaTerminal.Platform")]
    [InlineData("NovaTerminal.AgentHost.Contracts")]
    [InlineData("NovaTerminal.CommandAssist")]
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
            ("NovaTerminal.App", "NovaTerminal"),
            // CommandAssist is checked as a consumer here but is not a prefix *owner* in this loop:
            // its Views stay in the App by design, so the "one prefix, one assembly" rule is
            // asserted with that carve-out in
            // App_may_only_use_the_CommandAssist_prefix_for_Views below.
            ("NovaTerminal.CommandAssist", "NovaTerminal.CommandAssist"),
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

    /// <summary>
    /// <c>NovaTerminal.CommandAssist</c> is the only prefix deliberately shared between two
    /// assemblies: the assist assembly owns it, and the App keeps
    /// <c>NovaTerminal.CommandAssist.Views</c> because those are Avalonia <c>UserControl</c>s and
    /// the assist assembly must stay UI-toolkit-free. Anything else the App puts under that prefix
    /// is code that failed to move and should have.
    /// </summary>
    /// <remarks>
    /// Deliberately not filtered to public types. The archetypal leftover from an extraction is an
    /// <em>internal</em> helper the mechanical move missed, so a visibility filter here would let
    /// through exactly what this rule exists to catch. Compiler-generated types (XAML codegen,
    /// closure and iterator classes) are excluded instead: they are emitted under their declaring
    /// type's namespace, so flagging them would only restate the verdict on the type that owns
    /// them, and they are not code anyone can "move".
    /// </remarks>
    [Fact]
    public void App_may_only_use_the_CommandAssist_prefix_for_Views()
    {
        var result = Types.InAssembly(LoadByName("NovaTerminal"))
            .That().DoNotResideInNamespace("NovaTerminal.CommandAssist.Views")
            .And().DoNotHaveCustomAttribute(typeof(CompilerGeneratedAttribute))
            .Should()
            .NotResideInNamespaceStartingWith("NovaTerminal.CommandAssist")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "App may only own NovaTerminal.CommandAssist.Views; everything else under that prefix " +
            $"belongs in the CommandAssist assembly. Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
