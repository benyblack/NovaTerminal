using NovaTerminal.Shell;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.Tests;

public sealed class AnsiParserHardeningTests
{
    [Fact]
    public void UnknownEscSequence_IsIgnored_AndFollowingTextContinues()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1bZabc");

        Assert.Equal("abc", GetVisiblePlainText(buffer).Trim());
    }

    [Fact]
    public void Apc_Bel_TerminatesKittyQuery()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true);
        string? response = null;
        parser.OnResponse = value => response = value;

        parser.Process("\x1b_Ga=q,i=31\x07");

        Assert.NotNull(response);
        Assert.Contains(";ERR", response!);
    }

    [Fact]
    public void Dcs_Bel_TerminatesPayload()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var decoder = new RecordingImageDecoder();
        parser.ImageDecoder = decoder;

        parser.Process("\x1bPq#0!1~\x07");

        Assert.Equal("q#0!1~", decoder.LastSixelPayload);
    }

    [Fact]
    public void MalformedOsc_RecoversIntoFollowingCsiSequence()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b]0;bad-title\x1b[6 qX");

        Assert.Equal(CursorStyle.Beam, buffer.Modes.CursorStyle);
        Assert.Equal("X", GetVisiblePlainText(buffer).Trim());
    }

    [Fact]
    public void MalformedOsc_SplitAcrossCalls_RecoversIntoFollowingCsiSequence()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b]0;bad-title\x1b");
        parser.Process("[6 qX");

        Assert.Equal(CursorStyle.Beam, buffer.Modes.CursorStyle);
        Assert.Equal("X", GetVisiblePlainText(buffer).Trim());
    }

    [Fact]
    public void MalformedCsi_RecoversIntoFollowingCsiSequence()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[31\x1b[0mA");

        Assert.Equal("A", GetVisiblePlainText(buffer).Trim());
        Assert.True(buffer.IsDefaultForeground);
        Assert.False(buffer.IsBold);
    }

    [Fact]
    public void MalformedCsi_SplitAcrossCalls_RecoversBeforePrintableText()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[31\x1b");
        parser.Process("[0m");
        parser.Process("A");

        Assert.Equal("A", GetVisiblePlainText(buffer).Trim());
        Assert.True(buffer.IsDefaultForeground);
        Assert.False(buffer.IsBold);
    }

    [Theory]
    [InlineData("\x1bPq#0!1~\x1b", "[6 qX")]
    [InlineData("\x1b_Ga=q,i=31\x1b", "[6 qX")]
    public void MalformedStringSequence_SplitAcrossCalls_RecoversIntoFollowingCsiSequence(string firstChunk, string secondChunk)
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true);

        parser.Process(firstChunk);
        parser.Process(secondChunk);

        Assert.Equal(CursorStyle.Beam, buffer.Modes.CursorStyle);
        Assert.Equal("X", GetVisiblePlainText(buffer).Trim());
    }

    [Fact]
    public void NestedEscRecovery_DoesNotDoubleProcessOrSwallowNextValidSequence()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        int startedCount = 0;
        parser.OnCommandStarted = _ => startedCount++;

        parser.Process("\x1b]0;bad-title\x1b]133;B\x07X");

        Assert.Equal(1, startedCount);
        Assert.Equal("X", GetVisiblePlainText(buffer).Trim());
    }

    [Theory]
    [InlineData("\x1b[?u")]
    [InlineData("\x1b[>1u")]
    [InlineData("\x1b[<u")]
    [InlineData("\x1b[=1u")]
    public void PrefixedU_IsKittyKeyboardProtocol_DoesNotMoveCursor(string sequence)
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        // Save cursor at (0,0), then move elsewhere before the prefixed "u" under test.
        // These are kitty keyboard protocol query/push/pop/set sequences (#266); they mutate
        // the keyboard flag stacks and must NOT be treated as SCO restore-cursor. The flag
        // semantics themselves are covered by
        // tests/NovaTerminal.VT.Tests/KittyKeyboardProtocolTests.cs.
        parser.Process("\x1b[s");
        parser.Process("\x1b[10;20H");

        parser.Process(sequence);

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(9, buffer.CursorRow);
            Assert.Equal(19, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void PrefixedSaveCursor_XtSave_DoesNotOverwriteSavedCursor()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[5;5H"); // move to row 4, col 4 (0-based)
        parser.Process("\x1b[s");    // SCO save at (4,4)

        parser.Process("\x1b[10;10H"); // move to row 9, col 9
        parser.Process("\x1b[?1049s");  // XTSAVE - must NOT overwrite the SCO-saved cursor

        parser.Process("\x1b[u"); // SCO restore - should return to (4,4), not (9,9)

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(4, buffer.CursorRow);
            Assert.Equal(4, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void PrefixedRestore_XtRestore_DoesNotChangeScrollRegionOrCursor()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[5;20r");   // DECSTBM: scroll region rows 4..19 (0-based)
        parser.Process("\x1b[10;10H");  // move cursor to row 9, col 9

        parser.Process("\x1b[?1049r");  // XTRESTORE - must NOT be treated as DECSTBM

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(4, buffer.ScrollTop);
            Assert.Equal(19, buffer.ScrollBottom);
            Assert.Equal(9, buffer.CursorRow);
            Assert.Equal(9, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void PlainSaveAndRestoreCursor_StillWorks()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[3;7H"); // move to row 2, col 6
        parser.Process("\x1b[s");    // save

        parser.Process("\x1b[1;1H"); // move to origin

        parser.Process("\x1b[u"); // restore

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(2, buffer.CursorRow);
            Assert.Equal(6, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void PlainSetScrollingRegion_StillWorks()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[5;20r"); // DECSTBM rows 5..20 (1-based) -> 4..19 (0-based)

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(4, buffer.ScrollTop);
            Assert.Equal(19, buffer.ScrollBottom);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }


    // ---------------------------------------------------------------------------------------
    // #274: a CSI is identified by its final byte TOGETHER with its leader and intermediates.
    //
    // The two tests below sweep the WHOLE final-byte range rather than the list of finals the
    // parser happens to handle today. That is the point: #264 guarded s/u/r, the review of that
    // fix found eight more finals with the same hole, and both rounds were scoped by "the
    // spellings we currently know conflict". A sweep cannot be scoped that way - a new case added
    // to the CSI switch is covered by these the moment it exists, without anyone remembering to
    // extend a list.
    // ---------------------------------------------------------------------------------------

    /// <summary>Every byte that can terminate a CSI sequence (ECMA-48 final bytes, 0x40-0x7E).</summary>
    private static IEnumerable<char> AllCsiFinals
    {
        get
        {
            for (char c = '\x40'; c <= '\x7E'; c++) yield return c;
        }
    }

    /// <summary>
    /// The (final, leader) pairs that are genuinely defined in a prefixed form, so a state change
    /// from them is correct rather than a misparse. Everything not listed here must be inert.
    /// </summary>
    private static readonly HashSet<(char Final, char Leader)> DefinedPrefixedForms = new()
    {
        ('c', '>'), // DA2 - secondary device attributes, answered.
        ('h', '?'), // DEC private mode set.
        ('l', '?'), // DEC private mode reset.
        ('J', '?'), // DECSED - deliberately aliased to ED while DECSCA is unimplemented.
        ('K', '?'), // DECSEL - deliberately aliased to EL for the same reason.
        ('n', '?'), // DECXCPR - answered in the private form.
        ('u', '?'), // Kitty keyboard query - answers CSI ? Ps u. The '>', '<' and '=' kitty
                    // forms mutate only the keyboard flag stack, so they stay in the sweep
                    // and it asserts they never move the cursor.
    };

    [Fact]
    public void LeaderPrefixedCsi_NeverExecutesTheBareMeaning()
    {
        var offenders = new List<string>();

        foreach (char final in AllCsiFinals)
        {
            foreach (char leader in new[] { '?', '>', '<', '=' })
            {
                if (DefinedPrefixedForms.Contains((final, leader))) continue;

                // "1" as the parameter so that anything which does fire moves, erases or reports
                // something rather than defaulting to a no-op.
                string sequence = $"\x1b[{leader}1{final}";
                if (MutatesState(sequence, out string detail))
                {
                    offenders.Add($"CSI {leader}1{final} -> {detail}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Prefixed CSI sequences executed a bare meaning:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void IntermediatePrefixedCsi_NeverExecutesTheBareMeaning()
    {
        var offenders = new List<string>();

        foreach (char final in AllCsiFinals)
        {
            foreach (char intermediate in new[] { ' ', '"', '$', '!' })
            {
                // CSI Ps SP q is DECSCUSR and CSI Ps $ p is DECRQM: both are real sequences.
                if (final == 'q' && intermediate == ' ') continue;
                if (final == 'p' && intermediate == '$') continue;

                string sequence = $"\x1b[1{intermediate}{final}";
                if (MutatesState(sequence, out string detail))
                {
                    offenders.Add($"CSI 1{intermediate}{final} -> {detail}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Intermediate-bearing CSI sequences executed a bare meaning:\n" + string.Join("\n", offenders));
    }

    // ---------------------------------------------------------------------------------------
    // Named regressions. The sweep above would catch every one of these, but it reports them as
    // "state changed"; these say what the sequence actually is and what it did instead.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The live one. DA1 advertises sixel (CSI ?62;4;22c), so sixel-capable clients probe
    /// XTSMGRAPHICS as a matter of course - and every probe used to scroll the screen.
    /// </summary>
    [Theory]
    [InlineData("\x1b[?1;1;0S")]  // XTSMGRAPHICS: read sixel colour register count
    [InlineData("\x1b[?2;1;0S")]  // XTSMGRAPHICS: read sixel geometry
    [InlineData("\x1b[?1;4;0S")]
    public void XtSmGraphicsProbe_DoesNotScrollTheScreen(string probe)
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("line-one\r\nline-two\r\nline-three");

        parser.Process(probe);

        Assert.Equal("line-one\nline-two\nline-three", VisibleText(buffer));
    }

    [Fact]
    public void XtRmTitle_DoesNotScrollTheScreen()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("line-one\r\nline-two");

        parser.Process("\x1b[>2T"); // XTRMTITLE - reset title mode features. Not SD.

        Assert.Equal("line-one\nline-two", VisibleText(buffer));
    }

    [Fact]
    public void HighlightMouseTracking_IsNotScrollDown()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("line-one\r\nline-two");

        // xterm's initiate-highlight-mouse-tracking takes five parameters. SD takes one, so the
        // parameter count alone proves this is not SD.
        parser.Process("\x1b[1;2;3;4;5T");

        Assert.Equal("line-one\nline-two", VisibleText(buffer));
    }

    /// <summary>
    /// DECXCPR must be answered in the private form. A plain CPR is not a partial answer to it
    /// but a malformed one - the client is parsing for "CSI ? r ; c R".
    /// </summary>
    [Fact]
    public void DecXcpr_IsAnsweredInThePrivateForm()
    {
        var (_, parser) = NewTerminal();
        string? response = null;
        parser.OnResponse = value => response = value;

        parser.Process("\x1b[5;9H"); // row 5, column 9 (1-based)
        parser.Process("\x1b[?6n");  // DECXCPR

        Assert.Equal("\x1b[?5;9R", response);
    }

    [Fact]
    public void PlainCpr_IsStillAnsweredInTheBareForm()
    {
        var (_, parser) = NewTerminal();
        string? response = null;
        parser.OnResponse = value => response = value;

        parser.Process("\x1b[5;9H");
        parser.Process("\x1b[6n"); // DSR-CPR

        Assert.Equal("\x1b[5;9R", response);
    }

    /// <summary>
    /// DECOM makes cursor coordinates relative to the scrolling region, so a cursor report has to
    /// answer in that frame - CUP added ScrollTop on the way in, and the report subtracts it on
    /// the way out. Otherwise a client that homed to 1;1 inside a region is told it is at the
    /// region's absolute row and lays everything out against the wrong origin.
    ///
    /// This was already wrong for plain CPR before DECXCPR existed; DECXCPR would have inherited
    /// it, so both are pinned here.
    /// </summary>
    [Theory]
    [InlineData("\x1b[6n", "\x1b[2;3R")]    // CPR
    [InlineData("\x1b[?6n", "\x1b[?2;3R")]  // DECXCPR
    public void CursorReports_AreRelativeToTheScrollingRegionUnderOriginMode(string request, string expected)
    {
        var (_, parser) = NewTerminal();
        string? response = null;
        parser.OnResponse = value => response = value;

        parser.Process("\x1b[5;20r"); // scrolling region, rows 5..20 (1-based)
        parser.Process("\x1b[?6h");   // DECOM on
        parser.Process("\x1b[2;3H");  // row 2 OF THE REGION, column 3

        parser.Process(request);

        Assert.Equal(expected, response);
    }

    /// <summary>With DECOM off the same position reports absolutely.</summary>
    [Theory]
    [InlineData("\x1b[6n", "\x1b[2;3R")]
    [InlineData("\x1b[?6n", "\x1b[?2;3R")]
    public void CursorReports_AreAbsoluteWithoutOriginMode(string request, string expected)
    {
        var (_, parser) = NewTerminal();
        string? response = null;
        parser.OnResponse = value => response = value;

        parser.Process("\x1b[5;20r");
        parser.Process("\x1b[2;3H");

        parser.Process(request);

        Assert.Equal(expected, response);
    }

    /// <summary>CSI &gt; Ps n is xterm's disable-key-modifiers, not a status request.</summary>
    [Fact]
    public void XtermDisableModifiers_IsNotAnswered()
    {
        var (_, parser) = NewTerminal();
        string? response = null;
        parser.OnResponse = value => response = value;

        parser.Process("\x1b[>2n");

        Assert.Null(response);
    }

    /// <summary>
    /// DA3 asks for a DECRPTUI unit id. Answering it with a DA1 reply is answering a different
    /// question than the one asked, which is worse than staying silent.
    /// </summary>
    [Theory]
    [InlineData("\x1b[=c")] // DA3
    [InlineData("\x1b[<c")]
    public void UnsupportedDeviceAttributeRequests_AreNotAnsweredWithDa1(string request)
    {
        var (_, parser) = NewTerminal();
        string? response = null;
        parser.OnResponse = value => response = value;

        parser.Process(request);

        Assert.Null(response);
    }

    [Fact]
    public void PrimaryAndSecondaryDeviceAttributes_StillAnswer()
    {
        var (_, parser) = NewTerminal();
        var responses = new List<string>();
        parser.OnResponse = responses.Add;

        parser.Process("\x1b[c");
        parser.Process("\x1b[>c");

        Assert.Equal(new[] { "\x1b[?62;4;22c", "\x1b[>1;10;0c" }, responses);
    }

    /// <summary>DECSCA selects character protection; it is not a cursor style.</summary>
    [Fact]
    public void Decsca_DoesNotSetTheCursorStyle()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("\x1b[4 q"); // DECSCUSR: steady underline
        CursorStyle before = buffer.Modes.CursorStyle;

        parser.Process("\x1b[1\"q"); // DECSCA: mark characters protected

        Assert.Equal(before, buffer.Modes.CursorStyle);
    }

    [Fact]
    public void Decscusr_StillSetsTheCursorStyle()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[6 q"); // steady beam

        Assert.Equal(CursorStyle.Beam, buffer.Modes.CursorStyle);
    }

    /// <summary>The '&gt;', '&lt;' and '=' leaders used to fall through to the ANSI mode branch.</summary>
    [Theory]
    [InlineData("\x1b[>4h")]
    [InlineData("\x1b[<4h")]
    [InlineData("\x1b[=4h")]
    public void PrefixedSetMode_DoesNotSetAnsiModes(string sequence)
    {
        var (buffer, parser) = NewTerminal();

        parser.Process(sequence); // would have turned on IRM (insert mode)

        Assert.False(buffer.Modes.IsInsertMode);
    }

    [Fact]
    public void PlainSetAndResetMode_StillWork()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4h");
        Assert.True(buffer.Modes.IsInsertMode);

        parser.Process("\x1b[4l");
        Assert.False(buffer.Modes.IsInsertMode);
    }

    [Fact]
    public void DecPrivateModes_StillWork()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[?7l");
        Assert.False(buffer.Modes.IsAutoWrapMode);

        parser.Process("\x1b[?7h");
        Assert.True(buffer.Modes.IsAutoWrapMode);
    }

    /// <summary>
    /// The tab finals. These are the cases the state sweep above cannot see, because there is no
    /// public accessor for the tab stop set - so they are probed through the cursor instead.
    /// </summary>
    [Theory]
    [InlineData("\x1b[>3g")]
    [InlineData("\x1b[<3g")]
    [InlineData("\x1b[=3g")]
    [InlineData("\x1b[?3g")]
    public void PrefixedTabClear_DoesNotClearTabStops(string sequence)
    {
        var (buffer, parser) = NewTerminal();

        parser.Process(sequence); // TBC 3 would clear every tab stop

        parser.Process("\x1b[1;1H\t");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(8, buffer.CursorCol); // the default stop at column 8 survived
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void PlainTabClear_StillClearsTabStops()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[3g"); // TBC 3 - clear all
        parser.Process("\x1b[1;1H\t");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.NotEqual(8, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Theory]
    [InlineData("\x1b[>2I")]
    [InlineData("\x1b[=2I")]
    [InlineData("\x1b[>2Z")]
    [InlineData("\x1b[=2Z")]
    public void PrefixedTabMovement_DoesNotMoveTheCursor(string sequence)
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("\x1b[3;20H");

        parser.Process(sequence);

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(2, buffer.CursorRow);
            Assert.Equal(19, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>
    /// ECMA-48 puts SL, SR and PPA on the same finals as ICH, CUU and DCH, separated only by the
    /// space intermediate. None of the three is implemented; none may run the bare meaning.
    /// </summary>
    [Theory]
    [InlineData("\x1b[2 @")] // SL - scroll left, not ICH
    [InlineData("\x1b[2 A")] // SR - scroll right, not CUU
    [InlineData("\x1b[2 P")] // PPA - page position absolute, not DCH
    public void SpaceIntermediateSequences_DoNotRunTheBareMeaning(string sequence)
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("abcdef");
        parser.Process("\x1b[5;3H");
        string before = VisibleText(buffer);

        parser.Process(sequence);

        Assert.Equal(before, VisibleText(buffer));
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(4, buffer.CursorRow);
            Assert.Equal(2, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>DECSED/DECSEL stay aliased to ED/EL while DECSCA is unimplemented.</summary>
    [Fact]
    public void Decsed_StillErases_WhileNothingIsProtected()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("keep-me\r\nerase-me");
        parser.Process("\x1b[2;1H");

        parser.Process("\x1b[?0J"); // DECSED: erase from cursor to end of display

        Assert.Equal("keep-me", VisibleText(buffer));
    }

    [Fact]
    public void Decsel_StillErases_WhileNothingIsProtected()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("abcdefgh");
        parser.Process("\x1b[1;4H");

        parser.Process("\x1b[?0K"); // DECSEL: erase from cursor to end of line

        Assert.Equal("abc", VisibleText(buffer));
    }

    /// <summary>
    /// A private-parameter byte (0x3C-0x3F) is only meaningful as the leader, and no parameter
    /// byte may follow an intermediate. The VT500 state machine sends both to csi_ignore.
    /// </summary>
    /// <remarks>
    /// This is the identity guard's back door. HandleCsi reads a leader only in the first
    /// position, so CSI 1 ? 2 A had no leader, reached the parameter loop, had its '?' swallowed
    /// as a hard separator, and executed a bare CUU - a malformed private sequence running the
    /// bare meaning of a final byte.
    /// </remarks>
    [Theory]
    [InlineData("\x1b[1?2A")]  // private byte after a parameter
    [InlineData("\x1b[1>2A")]
    [InlineData("\x1b[1<2A")]
    [InlineData("\x1b[1=2A")]
    [InlineData("\x1b[2 3A")]  // parameter byte after an intermediate
    [InlineData("\x1b[2$3r")]
    public void MisplacedQualifierBytes_DiscardTheSequence(string sequence)
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("\x1b[3;22r");
        parser.Process("\x1b[10;40H");

        parser.Process(sequence);

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(9, buffer.CursorRow);
            Assert.Equal(39, buffer.CursorCol);
            Assert.Equal(2, buffer.ScrollTop);
            Assert.Equal(21, buffer.ScrollBottom);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>
    /// Sub-parameters use ':' (0x3A) and separators ';' (0x3B), both below the private range, so
    /// the guard above must not touch them.
    /// </summary>
    [Theory]
    [InlineData("\x1b[2;3H", 1, 2)]
    [InlineData("\x1b[2:3H", 1, 2)] // ':' is a separator here, as VtCapabilityContractTests pins for CHA
    public void OrdinaryParameterSeparators_StillParse(string sequence, int expectedRow, int expectedCol)
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("\x1b[10;40H");

        parser.Process(sequence);

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(expectedRow, buffer.CursorRow);
            Assert.Equal(expectedCol, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>
    /// A CSI whose parameter run hits the 64 KiB safety cap stops collecting bytes, but the final
    /// byte still arrives - and used to dispatch on the truncated prefix. An intermediate sitting
    /// past the cap is therefore invisible, so CSI &lt;64 KiB of digits&gt; SP A, which is SR, was
    /// classified as bare and executed CUU. A sequence that could not be read in full is now
    /// discarded: we do not know what it was.
    /// </summary>
    [Theory]
    [InlineData(' ', 'A')]  // SR, not CUU
    [InlineData(' ', '@')]  // SL, not ICH
    [InlineData(' ', 'P')]  // PPA, not DCH
    [InlineData('$', 'r')]  // DECCARA, not DECSTBM
    public void TruncatedCsi_IsDiscardedRatherThanClassifiedOnItsPrefix(char intermediate, char final)
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("alpha");
        parser.Process("\x1b[2;1H");
        parser.Process("bravo");
        parser.Process("\x1b[3;22r");
        parser.Process("\x1b[10;40H");
        string textBefore = VisibleText(buffer);

        // 65536 is MaxCsiParamChars, so the intermediate lands past the cap and is dropped.
        parser.Process("\x1b[" + new string('1', 65536) + intermediate + final);

        Assert.Equal(textBefore, VisibleText(buffer));
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(9, buffer.CursorRow);
            Assert.Equal(39, buffer.CursorCol);
            Assert.Equal(2, buffer.ScrollTop);
            Assert.Equal(21, buffer.ScrollBottom);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>
    /// The same rule with no intermediate involved. Once the cap is hit we cannot know whether
    /// what fell off was another digit or an intermediate, so the sequence goes rather than being
    /// executed on what survived.
    /// </summary>
    [Fact]
    public void TruncatedBareCsi_IsAlsoDiscarded()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("\x1b[10;40H");

        parser.Process("\x1b[" + new string('1', 70000) + "A");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(9, buffer.CursorRow);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>A long but complete parameter run still executes: the cap is not a length limit.</summary>
    [Fact]
    public void CsiJustUnderTheCap_StillExecutes()
    {
        var (buffer, parser) = NewTerminal();
        parser.Process("\x1b[10;40H");

        // Leading zeros keep the value at 5 while making the parameter run long.
        parser.Process("\x1b[" + new string('0', 65000) + "5A");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(4, buffer.CursorRow);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    // ---------------------------------------------------------------------------------------

    private static (TerminalBuffer Buffer, AnsiParser Parser) NewTerminal()
    {
        var buffer = new TerminalBuffer(80, 24);
        return (buffer, new AnsiParser(buffer));
    }

    private static string VisibleText(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return GetVisiblePlainText(buffer);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>
    /// Runs <paramref name="sequence"/> against a terminal arranged so that essentially any
    /// sequence which fires leaves a trace: text on screen to erase or scroll, a cursor parked
    /// away from every edge so all eight movement directions are visible, a non-default scroll
    /// region, and a recorder on the response channel.
    /// </summary>
    private static bool MutatesState(string sequence, out string detail)
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;

        parser.Process("alpha\r\nbravo\r\ncharlie\r\ndelta\r\necho");
        parser.Process("\x1b[3;22r");  // DECSTBM: a scroll region that is not the whole screen
        parser.Process("\x1b[10;40H"); // cursor mid-screen, far from every edge
        responses.Clear();

        string before = Snapshot(buffer, responses);
        parser.Process(sequence);
        string after = Snapshot(buffer, responses);

        detail = before == after ? string.Empty : $"state changed\n  before: {before}\n  after:  {after}";
        return before != after;
    }

    private static string Snapshot(TerminalBuffer buffer, List<string> responses)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            ModeState m = buffer.Modes;
            return string.Join("|",
                $"cursor={buffer.CursorRow},{buffer.CursorCol}",
                $"scroll={buffer.ScrollTop},{buffer.ScrollBottom}",
                $"insert={m.IsInsertMode}",
                $"origin={m.IsOriginMode}",
                $"wrap={m.IsAutoWrapMode}",
                $"lnm={m.IsLineFeedNewLineMode}",
                $"echo={m.IsEchoEnabled}",
                $"visible={m.IsCursorVisible}",
                $"style={m.CursorStyle}",
                $"paste={m.IsBracketedPasteMode}",
                $"appcursor={m.IsApplicationCursorKeys}",
                $"mouse={m.MouseModeX10}{m.MouseModeButtonEvent}{m.MouseModeAnyEvent}{m.MouseModeSGR}",
                $"focus={m.IsFocusEventReporting}",
                $"responses=[{string.Join(",", responses.Select(EscapeForMessage))}]",
                "text=" + EscapeForMessage(GetVisiblePlainText(buffer)));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string EscapeForMessage(string value)
    {
        return value.Replace("\x1b", "<ESC>").Replace("\n", "\\n");
    }

    private static string GetVisiblePlainText(TerminalBuffer buffer)
    {
        return string.Join("\n", buffer.ViewportRows.Select(GetRowText)).TrimEnd();
    }

    private static string GetRowText(TerminalRow row)
    {
        var chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
        return new string(chars).TrimEnd();
    }

    private sealed class RecordingImageDecoder : IImageDecoder
    {
        public string? LastSixelPayload { get; private set; }

        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;
            return null;
        }

        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight)
        {
            LastSixelPayload = sixelData;
            pixelWidth = 0;
            pixelHeight = 0;
            return null;
        }
    }
}
