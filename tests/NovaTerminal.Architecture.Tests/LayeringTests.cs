using System.Reflection;
using NetArchTest.Rules;

namespace NovaTerminal.Architecture.Tests;

public class LayeringTests
{
    private static Assembly Vt => typeof(global::NovaTerminal.VT.AnsiParser).Assembly;
    private static Assembly Replay => typeof(global::NovaTerminal.Replay.ReplayReader).Assembly;
    private static Assembly Rendering => typeof(global::NovaTerminal.Rendering.GlyphAtlas).Assembly;
    private static Assembly Pty => typeof(global::NovaTerminal.Pty.ITerminalSession).Assembly;
    private static Assembly Platform => typeof(global::NovaTerminal.Platform.Input.TerminalInputSender).Assembly;
    private static Assembly AgentHostContracts => typeof(global::NovaTerminal.AgentHost.Contracts.AgentHostProtocol).Assembly;
    private static Assembly CommandAssist => typeof(global::NovaTerminal.CommandAssist.Application.CommandAssistAnchorCalculator).Assembly;

    [Fact]
    public void Vt_must_be_a_leaf_assembly()
    {
        var result = Types.InAssembly(Vt)
            .Should()
            .NotHaveDependencyOnAny(
                "NovaTerminal.Replay",
                "NovaTerminal.Rendering",
                "NovaTerminal.Pty",
                "NovaTerminal.Platform",
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
                "NovaTerminal.Platform",
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
                "NovaTerminal.Platform",
                "NovaTerminal.App",
                "Avalonia",
                "SkiaSharp")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Replay may only reference VT. Offenders: {Join(result.FailingTypeNames)}");
    }

    [Fact]
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
    public void AgentHostContracts_must_be_a_leaf_assembly()
    {
        var result = Types.InAssembly(AgentHostContracts)
            .Should()
            .NotHaveDependencyOnAny(
                "NovaTerminal.VT",
                "NovaTerminal.Replay",
                "NovaTerminal.Rendering",
                "NovaTerminal.Pty",
                "NovaTerminal.Platform",
                "NovaTerminal.App",
                "Avalonia",
                "SkiaSharp")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"AgentHost.Contracts is a shared wire-contract leaf and must not depend on any " +
            $"production layer. Offenders: {Join(result.FailingTypeNames)}");
    }

    /// <summary>
    /// Command Assist was extracted from the App in #114 precisely so its suggestion, capture,
    /// storage and shell-integration logic could be reasoned about (and tested) without a UI
    /// toolkit. An <c>Avalonia</c> reference creeping back in silently undoes that: the App-side
    /// seams (<c>AssistKeyMapper</c>, <c>AssistRect</c>) only stay meaningful while this holds.
    /// The Views (<c>CommandAssist/Views/*.axaml</c>) deliberately remain in the App.
    /// </summary>
    [Fact]
    public void CommandAssist_must_not_depend_on_Avalonia_or_the_App()
    {
        var result = Types.InAssembly(CommandAssist)
            .Should()
            .NotHaveDependencyOnAny(
                "Avalonia",
                "SkiaSharp",
                // The App assembly is named "NovaTerminal" and its types live under the bare root,
                // so it cannot be named by assembly name here without matching CommandAssist itself.
                // Its two real buckets are enough to catch a reach back into the UI project.
                "NovaTerminal.Shell",
                "NovaTerminal.Controls",
                "NovaTerminal.Rendering",
                "NovaTerminal.Replay",
                "NovaTerminal.Pty")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"CommandAssist must stay UI-toolkit-free. Offenders: {Join(result.FailingTypeNames)}");
    }

    [Fact]
    public void No_production_assembly_references_test_assemblies()
    {
        foreach (var asm in new[] { Vt, Replay, Rendering, Pty, Platform, AgentHostContracts, CommandAssist })
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
