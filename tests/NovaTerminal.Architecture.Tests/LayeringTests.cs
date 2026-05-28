using System.Reflection;
using NetArchTest.Rules;

namespace NovaTerminal.Architecture.Tests;

public class LayeringTests
{
    private static Assembly Vt        => typeof(global::NovaTerminal.VT.AnsiParser).Assembly;
    private static Assembly Replay    => typeof(global::NovaTerminal.Replay.ReplayReader).Assembly;
    private static Assembly Rendering => typeof(global::NovaTerminal.Rendering.GlyphAtlas).Assembly;
    private static Assembly Pty       => typeof(global::NovaTerminal.Core.ITerminalSession).Assembly;
    private static Assembly Core      => typeof(global::NovaTerminal.Core.Input.TerminalInputSender).Assembly;

    [Fact]
    public void Vt_must_be_a_leaf_assembly()
    {
        var result = Types.InAssembly(Vt)
            .Should()
            .NotHaveDependencyOnAny(
                "NovaTerminal.Replay",
                "NovaTerminal.Rendering",
                "NovaTerminal.Pty",
                "NovaTerminal.Core",
                "NovaTerminal.App",
                "Avalonia",
                "SkiaSharp")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"VT must not depend on higher layers. Offenders: {Join(result.FailingTypeNames)}");
    }

    [Fact]
    public void Rendering_only_depends_on_Vt_and_Skia()
    {
        var result = Types.InAssembly(Rendering)
            .Should()
            .NotHaveDependencyOnAny(
                "NovaTerminal.Replay",
                "NovaTerminal.Pty",
                "NovaTerminal.Core",
                "NovaTerminal.App",
                "Avalonia")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Rendering may only reference VT + Skia. Offenders: {Join(result.FailingTypeNames)}");
    }

    [Fact]
    public void Replay_only_depends_on_Vt()
    {
        var result = Types.InAssembly(Replay)
            .Should()
            .NotHaveDependencyOnAny(
                "NovaTerminal.Rendering",
                "NovaTerminal.Pty",
                "NovaTerminal.Core",
                "NovaTerminal.App",
                "Avalonia",
                "SkiaSharp")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Replay may only reference VT. Offenders: {Join(result.FailingTypeNames)}");
    }

    // KNOWN VIOLATION (Section C.2 of the architecture review).
    // Today, Pty references VT (for `ITerminalSession.AttachBuffer(TerminalBuffer)`).
    // The fix is Phase 5 (ITerminalSession decomposition). Un-skip then.
    [Fact(Skip = "Known violation - fixed in Phase 5 of architecture-foundation-plan")]
    public void Pty_must_not_depend_on_Vt()
    {
        var result = Types.InAssembly(Pty)
            .Should()
            .NotHaveDependencyOn("NovaTerminal.VT")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Pty must not reference VT. Offenders: {Join(result.FailingTypeNames)}");
    }

    [Fact]
    public void No_production_assembly_references_test_assemblies()
    {
        foreach (var asm in new[] { Vt, Replay, Rendering, Pty, Core })
        {
            var result = Types.InAssembly(asm)
                .Should()
                .NotHaveDependencyOnAny("xunit", "xunit.v3", "Moq", "NetArchTest.Rules")
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"{asm.GetName().Name} must not reference test infrastructure. " +
                $"Offenders: {Join(result.FailingTypeNames)}");
        }
    }

    private static string Join(IEnumerable<string>? names)
        => names is null ? "(none)" : string.Join(", ", names);
}
