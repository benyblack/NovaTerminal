using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests;

public sealed class UnicodeWidthModelV2Tests
{
    [Fact]
    public void CombiningMark_Alone_HasZeroWidth()
    {
        var buffer = new TerminalBuffer(10, 4);

        Assert.Equal(0, buffer.GetGraphemeWidth("\u0301"));
    }

    [Fact]
    public void EmojiModifier_WrittenAcrossSeparateWrites_RemainsSingleWideCluster()
    {
        var buffer = new TerminalBuffer(10, 4);

        buffer.WriteContent("👍");
        buffer.WriteContent("🏽");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("👍🏽", buffer.GetGrapheme(0, 0));
            Assert.True(buffer.GetCell(0, 0).IsWide);
            Assert.True(buffer.GetCell(1, 0).IsWideContinuation);
            Assert.Equal(2, buffer.CursorCol);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void ZwjFamily_WrittenInChunks_RemainsSingleWideCluster()
    {
        var buffer = new TerminalBuffer(10, 4);

        buffer.WriteContent("👨");
        buffer.WriteContent("\u200D");
        buffer.WriteContent("👩");
        buffer.WriteContent("\u200D");
        buffer.WriteContent("👧");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("👨‍👩‍👧", buffer.GetGrapheme(0, 0));
            Assert.True(buffer.GetCell(0, 0).IsWide);
            Assert.True(buffer.GetCell(1, 0).IsWideContinuation);
            Assert.Equal(2, buffer.CursorCol);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void RegionalIndicatorPair_WrittenAcrossSeparateWrites_MergesIntoSingleFlagCluster()
    {
        var buffer = new TerminalBuffer(10, 4);

        buffer.WriteContent("🇺");
        buffer.WriteContent("🇸");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("🇺🇸", buffer.GetGrapheme(0, 0));
            Assert.True(buffer.GetCell(0, 0).IsWide);
            Assert.True(buffer.GetCell(1, 0).IsWideContinuation);
            Assert.Equal(' ', buffer.GetCell(2, 0).Character);
            Assert.Equal(2, buffer.CursorCol);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void VariationSelectors_RespectTextAndEmojiPresentation()
    {
        var buffer = new TerminalBuffer(10, 4);

        Assert.Equal(1, buffer.GetGraphemeWidth("❤"));
        Assert.Equal(1, buffer.GetGraphemeWidth("❤\uFE0E"));
        Assert.Equal(2, buffer.GetGraphemeWidth("❤\uFE0F"));

        buffer.WriteContent("❤");
        buffer.WriteContent("\uFE0F");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("❤️", buffer.GetGrapheme(0, 0));
            Assert.True(buffer.GetCell(0, 0).IsWide);
            Assert.True(buffer.GetCell(1, 0).IsWideContinuation);
            Assert.Equal(2, buffer.CursorCol);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void Backspace_OverWideGlyph_MovesToGraphemeStart()
    {
        var buffer = new TerminalBuffer(10, 4);

        buffer.WriteContent("あ");
        Assert.Equal(2, buffer.CursorCol);

        buffer.WriteChar('\b');

        Assert.Equal(0, buffer.CursorCol);
        Assert.False(buffer.IsPendingWrap);
    }

    [Fact]
    public void RightEdge_FlagSequence_WrittenTogether_StaysTwoCells()
    {
        var buffer = new TerminalBuffer(4, 4);

        buffer.SetCursorPosition(2, 0);
        buffer.WriteContent("🇺🇸");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("🇺🇸", buffer.GetGrapheme(2, 0));
            Assert.True(buffer.GetCell(2, 0).IsWide);
            Assert.True(buffer.GetCell(3, 0).IsWideContinuation);
            Assert.True(buffer.IsPendingWrap);
            Assert.Equal(3, buffer.CursorCol);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }
}
