namespace NovaTerminal.VT.Tests;

// Issue #266: kitty keyboard protocol (https://sw.kovidgoyal.net/kitty/keyboard-protocol/).
// Covers the four control sequences (query / push / pop / set), the per-screen-buffer flag
// stacks, the stack cap and eviction rule, RIS reset, and unsupported-bit masking.
public class KittyKeyboardProtocolTests
{
    private static (TerminalBuffer buffer, AnsiParser parser, List<string> responses) CreateTerminal()
    {
        var buffer = new TerminalBuffer(cols: 40, rows: 8);
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        return (buffer, parser, responses);
    }

    private static string Query(AnsiParser parser, List<string> responses)
    {
        responses.Clear();
        parser.Process("\x1b[?u");
        Assert.Single(responses);
        return responses[0];
    }

    [Fact]
    public void Query_WithProtocolDisabled_ReportsZeroFlags()
    {
        var (_, parser, responses) = CreateTerminal();

        Assert.Equal("\x1b[?0u", Query(parser, responses));
    }

    [Fact]
    public void Push_SetsDisambiguateFlag_AndQueryReportsIt()
    {
        var (buffer, parser, responses) = CreateTerminal();

        parser.Process("\x1b[>1u");

        Assert.True(buffer.Modes.KittyKeyboard.DisambiguateEscapeCodes);
        Assert.Equal("\x1b[?1u", Query(parser, responses));
    }

    [Fact]
    public void Push_WithoutParameters_PushesZeroFlags()
    {
        var (buffer, parser, responses) = CreateTerminal();

        parser.Process("\x1b[>1u");
        parser.Process("\x1b[>u");

        Assert.Equal(2, buffer.Modes.KittyKeyboard.StackDepth);
        Assert.Equal("\x1b[?0u", Query(parser, responses));
    }

    [Fact]
    public void Push_MasksFlagsWeDoNotImplement()
    {
        var (_, parser, responses) = CreateTerminal();

        // crossterm/Codex asks for disambiguate|report-event-types|report-alternate-keys.
        // We only honor disambiguate, so only that bit may be reported back - otherwise the
        // client would wait for key release events that never arrive.
        parser.Process("\x1b[>7u");

        Assert.Equal("\x1b[?1u", Query(parser, responses));
    }

    [Fact]
    public void Push_WithOnlyUnsupportedBits_ReportsZero()
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process("\x1b[>30u");

        Assert.Equal("\x1b[?0u", Query(parser, responses));
    }

    [Fact]
    public void Pop_RestoresPreviousFlags()
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process("\x1b[>1u");
        parser.Process("\x1b[>0u");
        Assert.Equal("\x1b[?0u", Query(parser, responses));

        parser.Process("\x1b[<1u");
        Assert.Equal("\x1b[?1u", Query(parser, responses));
    }

    [Fact]
    public void Pop_WithoutParameters_PopsOneEntry()
    {
        var (buffer, parser, _) = CreateTerminal();

        parser.Process("\x1b[>1u");
        parser.Process("\x1b[>1u");
        parser.Process("\x1b[<u");

        Assert.Equal(1, buffer.Modes.KittyKeyboard.StackDepth);
    }

    [Fact]
    public void Pop_BeyondBottomOfStack_ResetsAllFlags()
    {
        var (buffer, parser, responses) = CreateTerminal();

        parser.Process("\x1b[>1u");
        parser.Process("\x1b[<9u");

        Assert.Equal(0, buffer.Modes.KittyKeyboard.StackDepth);
        Assert.Equal("\x1b[?0u", Query(parser, responses));
    }

    [Fact]
    public void Set_DefaultMode_ReplacesAllFlags()
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process("\x1b[>1u");
        parser.Process("\x1b[=0u");

        Assert.Equal("\x1b[?0u", Query(parser, responses));
    }

    [Fact]
    public void Set_Mode2_OrsBitsIn()
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process("\x1b[=1;2u");

        Assert.Equal("\x1b[?1u", Query(parser, responses));
    }

    [Fact]
    public void Set_Mode3_ClearsBits()
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process("\x1b[>1u");
        parser.Process("\x1b[=1;3u");

        Assert.Equal("\x1b[?0u", Query(parser, responses));
    }

    [Fact]
    public void Set_OnEmptyStack_CreatesAnEntry()
    {
        var (buffer, parser, responses) = CreateTerminal();

        parser.Process("\x1b[=1;1u");

        Assert.Equal(1, buffer.Modes.KittyKeyboard.StackDepth);
        Assert.Equal("\x1b[?1u", Query(parser, responses));
    }

    [Fact]
    public void Push_BeyondStackCap_EvictsOldestEntry()
    {
        var (buffer, parser, responses) = CreateTerminal();

        // Bottom entry carries the disambiguate flag; the 32 pushes after it must evict it.
        parser.Process("\x1b[>1u");
        for (int i = 0; i < KittyKeyboardState.MaxStackDepth; i++)
        {
            parser.Process("\x1b[>0u");
        }

        Assert.Equal(KittyKeyboardState.MaxStackDepth, buffer.Modes.KittyKeyboard.StackDepth);

        // Draining the whole stack must land on "no flags", proving the evicted bottom entry
        // is gone rather than shifted up.
        parser.Process("\x1b[<40u");
        Assert.Equal(0, buffer.Modes.KittyKeyboard.StackDepth);
        Assert.Equal("\x1b[?0u", Query(parser, responses));
    }

    [Fact]
    public void AlternateScreen_KeepsItsOwnStack()
    {
        var (buffer, parser, responses) = CreateTerminal();

        parser.Process("\x1b[?1049h");   // enter alternate screen
        parser.Process("\x1b[>1u");      // editor raises the keyboard mode for itself
        Assert.Equal("\x1b[?1u", Query(parser, responses));
        Assert.True(buffer.Modes.KittyKeyboard.IsAltScreenActive);

        parser.Process("\x1b[?1049l");   // editor exits
        Assert.False(buffer.Modes.KittyKeyboard.IsAltScreenActive);
        Assert.Equal("\x1b[?0u", Query(parser, responses));
        Assert.Equal(0, buffer.Modes.KittyKeyboard.StackDepth);
    }

    [Fact]
    public void MainScreenStack_IsNotVisibleFromAlternateScreen()
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process("\x1b[>1u");      // shell raises the mode on the main screen
        parser.Process("\x1b[?1049h");
        Assert.Equal("\x1b[?0u", Query(parser, responses));

        parser.Process("\x1b[?1049l");
        Assert.Equal("\x1b[?1u", Query(parser, responses));
    }

    [Fact]
    public void AlternateScreenStack_SurvivesReEntry_ButDoesNotLeakToMain()
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process("\x1b[?1049h");
        parser.Process("\x1b[>1u");
        parser.Process("\x1b[?1049l");
        Assert.Equal("\x1b[?0u", Query(parser, responses));

        parser.Process("\x1b[?1049h");
        Assert.Equal("\x1b[?1u", Query(parser, responses));
    }

    [Fact]
    public void Ris_ClearsBothStacks()
    {
        var (buffer, parser, responses) = CreateTerminal();

        parser.Process("\x1b[?1049h");
        parser.Process("\x1b[>1u");
        parser.Process("\x1b[?1049l");
        parser.Process("\x1b[>1u");

        parser.Process("\u001bc");         // RIS

        Assert.Equal(0, buffer.Modes.KittyKeyboard.StackDepth);
        Assert.False(buffer.Modes.KittyKeyboard.IsAltScreenActive);
        Assert.Equal("\x1b[?0u", Query(parser, responses));

        parser.Process("\x1b[?1049h");
        Assert.Equal("\x1b[?0u", Query(parser, responses));
    }

    [Theory]
    [InlineData("\x1b[?u")]
    [InlineData("\x1b[>1u")]
    [InlineData("\x1b[<1u")]
    [InlineData("\x1b[=1;2u")]
    public void KittySequences_NeverMoveTheCursor(string sequence)
    {
        var (buffer, parser, _) = CreateTerminal();

        parser.Process("\x1b[s");        // SCO save at home
        parser.Process("\x1b[4;7H");     // move to row 3, col 6
        parser.Process(sequence);

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(3, buffer.CursorRow);
            Assert.Equal(6, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void PlainCsiU_IsStillScoRestoreCursor()
    {
        var (buffer, parser, _) = CreateTerminal();

        parser.Process("\x1b[3;5H");
        parser.Process("\x1b[s");
        parser.Process("\x1b[>1u");      // kitty push must not disturb the saved cursor
        parser.Process("\x1b[7;9H");
        parser.Process("\x1b[u");        // SCO restore

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(2, buffer.CursorRow);
            Assert.Equal(4, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void QueryReply_IsExactlyCsiQuestionFlagsU()
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process("\x1b[>1u");
        responses.Clear();
        parser.Process("\x1b[?u");

        Assert.Single(responses);
        Assert.Equal("\x1b[?1u", responses[0]);
        Assert.Equal(5, responses[0].Length);
    }
}
