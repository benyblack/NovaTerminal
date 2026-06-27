using NovaTerminal.McpServer.Tools;

namespace NovaTerminal.McpServer.Tests;

public class ExplainEscapeSequenceTests
{
    [Theory]
    [InlineData("ESC[2J", "ED")]
    [InlineData("\\x1b[2J", "ED")]
    [InlineData("CSI 2 J", "ED")]
    [InlineData("CSI H", "CUP")]
    [InlineData("CSI ?25h", "DECSET")]      // final byte 'h'
    [InlineData("CSI m", "SGR")]
    [InlineData("OSC 7", "working directory")]
    [InlineData("OSC 8", "Hyperlink")]
    [InlineData("ESC c", "RIS")]
    [InlineData("ESC [ 2 J", "ED")]          // space form
    [InlineData("ESC ] 7", "working directory")]
    [InlineData("ESC P q", "DCS")]           // 7-bit DCS introducer
    [InlineData("ESCP", "DCS")]              // no-space DCS introducer
    [InlineData("ESC _ G", "APC")]           // 7-bit APC introducer
    [InlineData("ESC_Ga=T", "APC")]          // no-space APC (Kitty)
    public void RecognizesCommonSequences(string seq, string expectedSubstring)
    {
        Assert.Contains(expectedSubstring, VtTools.ExplainEscapeSequence(seq), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Osc52_IsMarkedUnsupported()
    {
        // NovaTerminal's AnsiParser does not handle OSC 52; the explainer must not imply it does.
        var result = VtTools.ExplainEscapeSequence("OSC 52");
        Assert.Contains("NOT currently supported", result, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI E")] // CNL — not in HandleCsi
    [InlineData("CSI F")] // CPL — not in HandleCsi
    [InlineData("CSI b")] // REP — no handler
    public void UnhandledCsiSequences_AreMarkedUnsupported(string seq)
    {
        Assert.Contains("NOT currently handled", VtTools.ExplainEscapeSequence(seq), System.StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCsiFinalByte_IsReportedGracefully()
    {
        var result = VtTools.ExplainEscapeSequence("CSI 1 ~");
        Assert.Contains("final byte", result, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_PromptsForInput()
    {
        Assert.Contains("Provide a sequence", VtTools.ExplainEscapeSequence(""));
    }

    [Fact]
    public void Garbage_IsRejectedGracefully()
    {
        Assert.Contains("Unrecognized", VtTools.ExplainEscapeSequence("hello world"));
    }
}

public class VtTestPlanTests
{
    [Fact]
    public void TestPlan_IncludesKeySectionsAndFeature()
    {
        var plan = VtTools.GenerateVtTestPlan("OSC 8 hyperlinks");
        Assert.Contains("OSC 8 hyperlinks", plan, System.StringComparison.Ordinal);
        Assert.Contains("## Cases to cover", plan, System.StringComparison.Ordinal);
        Assert.Contains("## Where tests live", plan, System.StringComparison.Ordinal);
        Assert.Contains("## Verification", plan, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TestPlan_NullFeature_DoesNotThrow()
    {
        Assert.Contains("VT test plan", VtTools.GenerateVtTestPlan(null!));
    }
}

public class SuggestRelevantFilesTests
{
    [Theory]
    [InlineData("reflow edge cases", "TerminalBuffer.ReflowEngine.cs")]
    [InlineData("glyph atlas overflow", "GlyphAtlas.cs")]
    [InlineData("ssh key auth", "TerminalProfile.cs")]
    [InlineData("OSC parser sequence", "AnsiParser.cs")]
    [InlineData("theme validation", "src/NovaTerminal.App/Shell/ThemeManager.cs")] // correct path
    public void MapsTopicToFiles(string topic, string expectedFile)
    {
        Assert.Contains(expectedFile, WorkflowTools.SuggestRelevantFiles(topic), System.StringComparison.Ordinal);
    }

    [Fact]
    public void UnmappedTopic_FallsBackToArchitectureGuidance()
    {
        Assert.Contains("get_architecture_map", WorkflowTools.SuggestRelevantFiles("the gizmo widget"));
    }

    [Fact]
    public void NullTopic_DoesNotThrow()
    {
        // No keyword match → fallback guidance; must not throw.
        Assert.Contains("architecture", WorkflowTools.SuggestRelevantFiles(null!), System.StringComparison.OrdinalIgnoreCase);
    }
}
