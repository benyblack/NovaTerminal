using NovaTerminal.McpServer.Tools;
using NovaTerminal.VtContract;

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
    [InlineData("CSI b")] // REP — no handler
    public void UnhandledCsiSequences_AreMarkedUnsupported(string seq)
    {
        Assert.Contains("NOT currently handled", VtTools.ExplainEscapeSequence(seq), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI E", "CNL")]
    [InlineData("CSI F", "CPL")]
    [InlineData("CSI G", "CHA")]
    public void SupportedCursorCapabilities_AreReportedFromContract(string sequence, string mnemonic)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.Contains(mnemonic, result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("NOT currently handled", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("unsupported", result, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CSI ?2E", "CNL")]
    [InlineData("CSI ?2F", "CPL")]
    [InlineData("CSI 2$E", "CNL")]
    [InlineData("CSI 2$F", "CPL")]
    public void QualifiedContractForms_AreNotReportedAsTheSupportedBareCapability(string sequence, string mnemonic)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.DoesNotContain(mnemonic, result, System.StringComparison.Ordinal);
        Assert.Contains("not in the curated table", result, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CSI 2;3E", "CNL")]
    [InlineData("CSI 2;3F", "CPL")]
    [InlineData("CSI 2;3G", "CHA")]
    [InlineData("CSI 2:3E", "CNL")]
    [InlineData("CSI 2:3F", "CPL")]
    [InlineData("CSI 2:3G", "CHA")]
    public void ParameterLists_ReportTheCapabilityAcceptedByTheParser(string sequence, string mnemonic)
    {
        Assert.Contains(mnemonic, VtTools.ExplainEscapeSequence(sequence), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// #274: the curated fallback table is keyed by final byte alone, so it used to describe
    /// XTSMGRAPHICS as SU and XTRMTITLE as SD. The parser ignores both, so those answers told the
    /// reader the opposite of what NovaTerminal does.
    /// </summary>
    [Theory]
    [InlineData("CSI ?1;1;0S", "SU")] // XTSMGRAPHICS, not Scroll Up
    [InlineData("CSI >2T", "SD")]     // XTRMTITLE, not Scroll Down
    [InlineData("CSI <1P", "DCH")]
    [InlineData("CSI =1d", "VPA")]
    // Only leaders are covered here. ExplainEscapeSequence strips whitespace the user added
    // for readability ("CSI 2 J"), so a space intermediate cannot be expressed in its input
    // syntax at all - CSI Pn SP @ (SL) is indistinguishable from a legibly-typed ICH. That is
    // a deliberate trade in the tool's input handling, not something this change alters.
    public void QualifiedForms_AreNotExplainedAsTheBareFinalByte(string sequence, string bareName)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.DoesNotContain(bareName, result, System.StringComparison.Ordinal);
        Assert.Contains("selects a different function", result, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A private-parameter byte outside the leader position, or a parameter byte after an
    /// intermediate, is malformed - the parser discards the whole sequence. The explainer has to
    /// say that rather than describe it as another function, which would be a different wrong
    /// answer to the same question.
    /// </summary>
    [Theory]
    [InlineData("CSI 1?2A")]
    [InlineData("CSI 1>2A")]
    [InlineData("CSI 2$3r")]
    public void MalformedQualifierPositions_AreReportedAsMalformed(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.Contains("malformed", result, System.StringComparison.Ordinal);
        Assert.Contains("discards the whole sequence", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("CUU", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("DECSTBM", result, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Parameter count selects a function too, and no leader or intermediate is involved:
    /// CSI 1;2;3;4;5 T is xterm highlight-mouse tracking, which the parser ignores, but it reads
    /// as a plain parameter list so the qualifier gate cannot see it. It used to be described
    /// as SD.
    /// </summary>
    [Fact]
    public void FiveParameterT_IsExplainedAsHighlightMouseTracking()
    {
        string result = VtTools.ExplainEscapeSequence("CSI 1;2;3;4;5T");

        Assert.DoesNotContain("Scroll Down Ps lines", result, System.StringComparison.Ordinal);
        Assert.Contains("highlight-mouse-tracking", result, System.StringComparison.Ordinal);
        Assert.Contains("ignores it", result, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Two to four parameters on 'T' is neither SD (one parameter) nor highlight-mouse-tracking
    /// (exactly five). The parser ignores it, and the explainer must not pick either name -
    /// trading one wrong answer for another is not an improvement.
    /// </summary>
    [Theory]
    [InlineData("CSI 1;2T")]
    [InlineData("CSI 1;2;3T")]
    [InlineData("CSI 1;2;3;4T")]
    [InlineData("CSI 1:2T")]
    [InlineData("CSI 1:2:3T")]
    [InlineData("CSI 1;2:3T")]
    [InlineData("CSI 1;2;3;4;5;6T")]
    public void ParameterCountsMatchingNoDefinedForm_AreNotGivenOne(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.DoesNotContain("Scroll Down Ps lines", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("highlight-mouse-tracking", result, System.StringComparison.Ordinal);
        Assert.Contains("matches no defined form", result, System.StringComparison.Ordinal);
    }

    /// <summary>The single-parameter and no-parameter spellings are still SD.</summary>
    [Theory]
    [InlineData("CSI T")]
    [InlineData("CSI 5T")]
    public void ScrollDown_StillResolves(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.Contains("Scroll Down Ps lines", result, System.StringComparison.Ordinal);
    }

    /// <summary>The qualified forms that ARE defined must keep resolving.</summary>
    [Theory]
    [InlineData("CSI ?25h", "DECSET")]
    [InlineData("CSI ?1049l", "DECRST")]
    [InlineData("CSI >c", "DA")]
    [InlineData("CSI 6 q", "DECSCUSR")]
    [InlineData("CSI ?2J", "ED")]      // DECSED, still aliased to ED while DECSCA is unimplemented
    [InlineData("CSI ?6n", "DSR")]     // DECXCPR
    public void DefinedQualifiedForms_StillResolve(string sequence, string expected)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.Contains(expected, result, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// #274: a leader or an intermediate selects a different function, so the explainer must not
    /// describe these as CHA. It used to, reporting them as a "qualified form" that NovaTerminal
    /// processed as CHA - which mirrored the parser's missing leader guard. The parser ignores
    /// them now, so claiming CHA would send a reader looking for a cursor move that never happens.
    /// </summary>
    [Theory]
    [InlineData("CSI ?2G")]
    [InlineData("CSI 2$G")]
    public void QualifiedChaForms_AreNotExplainedAsCha(string sequence)
    {
        string result = VtTools.ExplainEscapeSequence(sequence);

        Assert.DoesNotContain("CHA", result, System.StringComparison.Ordinal);
        Assert.Contains("not in the curated table", result, System.StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogCapabilities_AgreeWithExplainerSupportClaims()
    {
        foreach (VtCapability capability in VtCapabilityCatalog.All)
        {
            string result = VtTools.ExplainEscapeSequence(capability.Key.Replace(':', ' '));

            Assert.Contains(capability.Mnemonic, result, System.StringComparison.Ordinal);
            if (capability.Support == VtSupport.Supported)
            {
                Assert.DoesNotContain("NOT currently handled", result, System.StringComparison.Ordinal);
                Assert.DoesNotContain("unsupported", result, System.StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void CsiIntermediateBytes_AreSurfaced()
    {
        // A real (punctuation) intermediate byte must be surfaced, e.g. DECSTR `CSI ! p`.
        var result = VtTools.ExplainEscapeSequence("CSI ! p");
        Assert.Contains("intermediate", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("!", result, System.StringComparison.Ordinal);
    }

    [Fact]
    public void FormattingSpaces_AreNotTreatedAsIntermediates()
    {
        // "CSI 2 J" has only a formatting space; it must resolve to ED with no intermediate note.
        var result = VtTools.ExplainEscapeSequence("CSI 2 J");
        Assert.Contains("ED", result, System.StringComparison.Ordinal);
        Assert.DoesNotContain("intermediate byte", result, System.StringComparison.OrdinalIgnoreCase);
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
