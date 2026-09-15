using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// #236: <c>_isAfterZwj</c> means "the cluster just written ended expecting a continuation", and the
/// write path uses it to attach whatever arrives next to the previous cell. It was computed by
/// scanning the cluster for a ZWJ *anywhere*, so a *completed* ZWJ sequence — which contains ZWJ but
/// does not end with one — left the flag set and swallowed the following character.
///
/// The flag exists for split reads: a ZWJ can arrive at the tail of one PTY read with the emoji that
/// completes the join in the next. Both directions are asserted here, because narrowing the check is
/// only correct if the incomplete case still joins.
/// </summary>
public class ZwjContinuationTests
{
    private const string Zwj = "\u200D";
    private const string Man = "\U0001F468";
    private const string Woman = "\U0001F469";
    private const string Girl = "\U0001F467";
    private const string ThumbsUp = "\U0001F44D";
    private const string Family = Man + Zwj + Woman + Zwj + Girl;

    [Fact]
    public void SpaceAfterCompletedZwjSequenceLandsInItsOwnCell()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process(Family + " x");

        buffer.Lock.EnterReadLock();
        try
        {
            // The family is one cluster in cell 0 and occupies two columns, so cell 1 is its
            // continuation and the space is the first thing to get a cell of its own.
            Assert.Equal(Family, buffer.GetGrapheme(0, 0));
            Assert.Equal(" ", buffer.GetGrapheme(2, 0));
            Assert.Equal("x", buffer.GetGrapheme(3, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void LetterImmediatelyAfterCompletedZwjSequenceLandsInItsOwnCell()
    {
        // The space in the repro is not special; nothing at all should attach.
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process(Family + "ab");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(Family, buffer.GetGrapheme(0, 0));
            Assert.Equal("a", buffer.GetGrapheme(2, 0));
            Assert.Equal("b", buffer.GetGrapheme(3, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void EmojiAfterCompletedZwjSequenceStartsANewCluster()
    {
        // The worst case for the old behaviour: the following grapheme is itself joinable, so it
        // merged into a family that was already complete and produced one enormous cluster.
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process(Family + ThumbsUp);

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(Family, buffer.GetGrapheme(0, 0));
            Assert.Equal(ThumbsUp, buffer.GetGrapheme(2, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void TrailingZwjSplitAcrossReadsStillJoins()
    {
        // The case the flag is for. Grapheme segmentation keeps a trailing ZWJ attached to its base
        // (GB9), so the first read produces the single cluster "man + ZWJ" — which really does end
        // expecting a continuation — and the second read must merge into it rather than take a cell.
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process(Man + Zwj);
        parser.Process(Woman);

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(Man + Zwj + Woman, buffer.GetGrapheme(0, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void ZwjArrivingAloneBetweenReadsStillJoins()
    {
        // Three-way split: the ZWJ is its own read, so it is its own single-rune cluster.
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process(Man);
        parser.Process(Zwj);
        parser.Process(Woman);

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(Man + Zwj + Woman, buffer.GetGrapheme(0, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void ThreeWayJoinAssembledAcrossReadsProducesOneCluster()
    {
        // Each read ends on a ZWJ, so the flag has to survive two consecutive attachments. The
        // attach path recomputes the flag from the *incoming* piece, not the merged result, which is
        // what makes this work.
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process(Man + Zwj);
        parser.Process(Woman + Zwj);
        parser.Process(Girl);

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(Family, buffer.GetGrapheme(0, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void CharacterAfterASequenceCompletedAcrossReadsLandsInItsOwnCell()
    {
        // Combines both halves: the join is assembled from split reads, and once it is complete the
        // next character must stop attaching. A fix that simply cleared the flag after one
        // attachment would pass the split-read tests above and fail this one.
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process(Man + Zwj);
        parser.Process(Woman);
        parser.Process("x");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(Man + Zwj + Woman, buffer.GetGrapheme(0, 0));
            Assert.Equal("x", buffer.GetGrapheme(2, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
