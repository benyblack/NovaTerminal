using System.Reflection;
using NetArchTest.Rules;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Each production assembly should put its types in a namespace that matches its assembly name.
/// Today, 5 of 6 production assemblies all use "NovaTerminal.Core" as their root namespace.
/// Phase 3 fixes this one assembly at a time.
/// </summary>
public class NamespaceAlignmentTests
{
    private static Assembly LoadByName(string name)
        => AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == name);

    [Fact(Skip = "Known violation - fixed in Phase 3 (VT subphase)")]
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

    [Fact(Skip = "Known violation - fixed in Phase 3 (Replay subphase)")]
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

    [Fact(Skip = "Known violation - fixed in Phase 3 (Rendering subphase)")]
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

    [Fact(Skip = "Known violation - fixed in Phase 3 (Pty subphase)")]
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
}
