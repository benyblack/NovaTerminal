using System.Reflection;
using NetArchTest.Rules;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Each production assembly should put its types in a namespace that matches its assembly name.
/// Today, 5 of 6 production assemblies all use "NovaTerminal.Platform" as their root namespace.
/// Phase 3 fixes this one assembly at a time.
/// </summary>
public class NamespaceAlignmentTests
{
    private static Assembly LoadByName(string name) => Assembly.Load(name);

    [Fact]
    public void All_VT_types_use_NovaTerminal_VT_namespace()
    {
        var result = Types.InAssembly(LoadByName("NovaTerminal.VT"))
            .That()
            .DoNotResideInNamespace("System.Runtime.CompilerServices")
            .And().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith("NovaTerminal.VT")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"VT types not in NovaTerminal.VT.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void All_Replay_types_use_NovaTerminal_Replay_namespace()
    {
        var result = Types.InAssembly(LoadByName("NovaTerminal.Replay"))
            .That().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith("NovaTerminal.Replay")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Replay types not in NovaTerminal.Replay.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void All_Rendering_types_use_NovaTerminal_Rendering_namespace()
    {
        var result = Types.InAssembly(LoadByName("NovaTerminal.Rendering"))
            .That().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith("NovaTerminal.Rendering")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Rendering types not in NovaTerminal.Rendering.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void All_Pty_types_use_NovaTerminal_Pty_namespace()
    {
        var result = Types.InAssembly(LoadByName("NovaTerminal.Pty"))
            .That().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith("NovaTerminal.Pty")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Pty types not in NovaTerminal.Pty.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Only_the_Core_assembly_uses_NovaTerminal_Core_namespace()
    {
        foreach (var asmName in new[] { "NovaTerminal.VT", "NovaTerminal.Replay",
                                         "NovaTerminal.Rendering", "NovaTerminal.Pty" })
        {
            var result = Types.InAssembly(LoadByName(asmName))
                .That().ArePublic()
                .Should()
                .NotResideInNamespaceStartingWith("NovaTerminal.Platform")
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"{asmName} must not use NovaTerminal.Platform namespace. " +
                $"Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }
}
