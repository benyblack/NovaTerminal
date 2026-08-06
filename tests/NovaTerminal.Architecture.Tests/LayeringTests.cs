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
                "NovaTerminal.Pty",
                // CommandAssist references neither today; naming them keeps the allowlist
                // complete so a future "just grab the parser" shortcut fails here first.
                "NovaTerminal.VT",
                "NovaTerminal.Platform")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"CommandAssist must stay UI-toolkit-free. Offenders: {Join(result.FailingTypeNames)}");
    }

    /// <summary>
    /// The V2 Phase 5 AI seam defines <c>IAssistContentProvider</c> without shipping a provider, and
    /// the design doc asks for this test by name: "no network code, no API clients, no model
    /// selection in V2". The risk it guards is not that someone writes an HTTP client on purpose - it
    /// is that "just fetch the tldr page if it is missing" or "just check for an update" arrives as a
    /// two-line convenience inside a class nobody thinks of as networking, and the assembly that
    /// holds the user's command history and the redaction filter quietly gains the ability to leave
    /// the machine.
    /// </summary>
    /// <remarks>
    /// When an AI provider is eventually built it does <em>not</em> relax this: it goes in its own
    /// assembly behind <c>IAssistContentProvider</c>, which is what the seam is for. This test
    /// failing is the signal that a milestone skipped that step.
    /// </remarks>
    [Fact]
    public void CommandAssist_must_not_depend_on_networking()
    {
        var result = Types.InAssembly(CommandAssist)
            .Should()
            .NotHaveDependencyOnAny(
                "System.Net",
                "System.Net.Http",
                "System.Net.Sockets",
                "System.Net.WebSockets",
                "System.Net.Security",
                "System.Net.NetworkInformation",
                "Microsoft.Extensions.Http",
                "Grpc",
                "RestSharp",
                "Flurl",
                "Refit")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"CommandAssist must contain no networking code (V2 Phase 5 exit criterion). " +
            $"Offenders: {Join(result.FailingTypeNames)}");
    }

    /// <summary>
    /// The IL sibling above only sees types the compiler actually emitted a reference to. A
    /// <c>using System.Net.Http;</c> with no live call site, or a package reference added ahead of
    /// the code that will use it, leaves no type dependency and would pass there. The assembly
    /// reference table catches the edge itself.
    /// </summary>
    [Fact]
    public void CommandAssist_assembly_references_no_networking_assemblies()
    {
        string[] offenders = CommandAssist.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name =>
                name.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Microsoft.Extensions.Http", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Grpc", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"CommandAssist must not reference networking assemblies. Offenders: {Join(offenders)}");
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
