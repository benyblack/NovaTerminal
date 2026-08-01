using NovaTerminal.Shell;
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
        parser.OnCommandStarted = () => startedCount++;

        parser.Process("\x1b]0;bad-title\x1b]133;B\x07X");

        Assert.Equal(1, startedCount);
        Assert.Equal("X", GetVisiblePlainText(buffer).Trim());
    }

    [Theory]
    [InlineData("\x1b[?u")]
    [InlineData("\x1b[>1u")]
    [InlineData("\x1b[<u")]
    [InlineData("\x1b[=1u")]
    public void PrefixedRestoreCursor_IsIgnored_DoesNotMoveCursor(string sequence)
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);

        // Save cursor at (0,0), then move elsewhere before the prefixed "u" under test.
        // A kitty keyboard query/push/pop/set must NOT be treated as SCO restore-cursor.
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
