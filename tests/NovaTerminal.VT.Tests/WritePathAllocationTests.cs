using System;
using NovaTerminal.VT;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// #165: every printable character of all output went through
/// <c>StringInfo.GetTextElementEnumerator</c> + <c>GetTextElement()</c> — a boxed enumerator plus a
/// string allocation per grapheme, which for plain ASCII is one string per character. Cat-ing a large
/// file allocated on the order of gigabytes of garbage.
///
/// These assert on <see cref="GC.GetAllocatedBytesForCurrentThread"/> rather than on wall-clock time,
/// so they are deterministic and meaningful on a shared CI runner. The ceilings are per-character
/// budgets with generous headroom: the point is to catch a return to per-character *string*
/// allocation, not to pin an exact number that innocuous changes would break.
/// </summary>
public class WritePathAllocationTests
{
    /// Allocated bytes attributable to <paramref name="action"/>, after a warm-up pass so first-call
    /// JIT and lazily-initialized Unicode tables are not counted.
    private static long MeasureAllocations(Action warmup, Action action)
    {
        warmup();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // Rows are sized to hold every line these tests write, so nothing scrolls off. That matters: my
    // first version used a 50-row viewport and 200 lines, and the numbers were dominated by
    // scrollback *page* allocation as rows evicted - 2.5 KB for 50 lines but 496 KB for 200. That
    // measures paging, not the write path. Keeping the output resident isolates the thing under test.
    private const int Cols = 200;
    private const int Lines = 250;
    private const int Rows = Lines + 10;

    [Fact]
    public void AsciiOutput_DoesNotAllocatePerCharacter()
    {
        string line = new string('a', Cols);
        var buffer = new TerminalBuffer(Cols, Rows);

        long allocated = MeasureAllocations(
            warmup: () => buffer.WriteContent(line),
            action: () =>
            {
                for (int i = 0; i < Lines; i++)
                {
                    buffer.WriteContent(line);
                }
            });

        long characters = (long)Lines * Cols;
        double perChar = allocated / (double)characters;

        // Before the fix this was one string per character - 8 bytes of object header, 8 of length
        // and padding, 2 of payload, so ~24 bytes each - plus a boxed enumerator per call. A budget
        // of 2 B/char is comfortably below that and comfortably above the ~0.25 B/char measured
        // after the fix, so it discriminates without being brittle.
        Assert.True(
            perChar < 2.0,
            $"allocated {allocated} bytes for {characters} ASCII characters ({perChar:F2} B/char). "
            + "A per-character string allocation in the write path is back (#165).");
    }

    [Fact]
    public void AsciiOutput_AllocationDoesNotScaleWithCharacterCount()
    {
        // Shape test, independent of any absolute budget: per-character allocation grows linearly
        // with the character count, so 5x the output would mean ~5x the garbage. With the fix the
        // remaining allocation is essentially fixed setup, so the two measurements stay close.
        string line = new string('x', Cols);

        // Buffers are constructed *outside* the measured region. My first version built them inside,
        // and a 260-row buffer allocation dwarfed the per-character difference — the ratio stayed
        // near 1 whether or not the write path allocated per character, so the test passed against
        // the pre-fix code and was measuring nothing.
        var smallBuffer = new TerminalBuffer(Cols, Rows);
        var largeBuffer = new TerminalBuffer(Cols, Rows);

        long small = MeasureAllocations(
            warmup: () => smallBuffer.WriteContent(line),
            action: () =>
            {
                for (int i = 0; i < 50; i++) smallBuffer.WriteContent(line);
            });

        long large = MeasureAllocations(
            warmup: () => largeBuffer.WriteContent(line),
            action: () =>
            {
                for (int i = 0; i < 250; i++) largeBuffer.WriteContent(line);
            });

        // 5x the characters. Linear-in-characters would be ~5x; a 2x multiplier plus a small absolute
        // slack admits the fixed cost without admitting per-character strings.
        //
        // The slack is not cosmetic: after the fix both measurements are *zero*, and `0 < 0` is false,
        // so a bare ratio made the test fail on a perfect result. Caught by running it against the
        // fixed code rather than only against the mutant.
        Assert.True(
            large <= small * 2 + 4096,
            $"5x the output allocated {large} bytes vs {small} for the baseline - allocation is "
            + "scaling with character count rather than being fixed setup (#165).");
    }

    [Fact]
    public void GraphemeClustersStillRenderCorrectly()
    {
        // The allocation work replaced GetTextElementEnumerator with GetNextTextElementLength plus an
        // ASCII fast path. The fast path claims that an ASCII character followed by another ASCII
        // character is a complete cluster; anything non-ASCII must still go through full
        // segmentation. This is the behavioural guard on that claim.
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process("ábc");   // 'a' + combining acute, then plain ASCII

        buffer.Lock.EnterReadLock();
        try
        {
            // The combining mark must have joined the 'a', not consumed a cell of its own.
            Assert.Equal("á", buffer.GetGrapheme(0, 0));
            Assert.Equal("b", buffer.GetGrapheme(1, 0));
            Assert.Equal("c", buffer.GetGrapheme(2, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void AsciiFastPathDoesNotSwallowAFollowingCombiningMark()
    {
        // The dangerous edge for the fast path: ASCII base immediately followed by a non-ASCII
        // combining mark. If the fast path took the ASCII char alone in that case, the mark would
        // land in its own cell.
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process("é");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("é", buffer.GetGrapheme(0, 0));
            Assert.Equal(" ", buffer.GetGrapheme(1, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void AstralAndZwjSequencesStillFormSingleClusters()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        // A ZWJ family, then a following character. This originally wrote the family on its own,
        // because `IsLastRuneZwj` returned true for *any* ZWJ in the cluster and the trailing space
        // got merged into the family - a real bug the test tripped over, filed as #236 and fixed
        // since. Appending `z` again now that the flag means what its name says.
        parser.Process("\U0001F468‍\U0001F469‍\U0001F467z");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("\U0001F468‍\U0001F469‍\U0001F467", buffer.GetGrapheme(0, 0));
            Assert.Equal("z", buffer.GetGrapheme(2, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void AstralEmojiFormsOneClusterAndOccupiesTwoCells()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process("a\U0001F44Db");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("a", buffer.GetGrapheme(0, 0));
            Assert.Equal("\U0001F44D", buffer.GetGrapheme(1, 0));
            Assert.Equal("b", buffer.GetGrapheme(3, 0)); // 2 is the wide continuation
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
