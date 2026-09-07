using NovaTerminal.VtContract;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// One executable contract per capability the catalog advertises as
/// <see cref="VtSupport.Supported"/>: proof that something in the parser actually implements what
/// the catalog claims, rather than the claim standing alone.
/// </summary>
/// <remarks>
/// <para>
/// These assertions are <b>descriptive</b> - they record what the parser does today, not what a
/// specification says it ought to do. So when a change alters accepted behaviour on purpose,
/// updating the affected lines here is part of that change, not a warning sign. What must not
/// happen is the reverse: an assertion edited to make a build pass without the behaviour change
/// being intended and stated.
/// </para>
/// <para>
/// The distinction has bitten once, which is why it is written down. 816489b added
/// <c>AssertPosition("\x1b[?2G", ...)</c> and <c>AssertPosition("\x1b[2$G", ...)</c> by
/// transcribing what the parser happened to do at the time - CHA had no leader guard - while the
/// identical shapes for CNL and CPL three lines up had said <c>AssertIgnored</c> since b7a00dc.
/// The file contradicted itself for eight days. #433 changed the G lines to match, because a
/// leader or an intermediate makes a sequence a different sequence and neither
/// <c>CSI ? Ps G</c> nor <c>CSI Ps $ G</c> is defined. Behaviour here is also swept
/// systematically over every final byte in <c>AnsiParserHardeningTests</c>, so a contract line and
/// the sweep disagreeing is a real signal worth stopping for.
/// </para>
/// </remarks>
public sealed class VtCapabilityContractTests
{
    private static readonly Dictionary<string, Action> ContractCases =
        new(StringComparer.Ordinal)
        {
            ["cursor-next-line"] = AssertCursorNextLine,
            ["cursor-previous-line"] = AssertCursorPreviousLine,
            ["cursor-horizontal-absolute"] = AssertCursorHorizontalAbsolute,
        };

    private static readonly Dictionary<string, string> ContractSequences =
        new(StringComparer.Ordinal)
        {
            ["cursor-next-line"] = "\x1b[2E",
            ["cursor-previous-line"] = "\x1b[2F",
            ["cursor-horizontal-absolute"] = "\x1b[4G",
        };

    public static IEnumerable<object[]> SupportedCapabilities()
        => VtCapabilityCatalog.All
            .Where(capability => capability.Support == VtSupport.Supported)
            .Select(capability => new object[]
            {
                capability.Key,
                capability.ContractCase!,
            });

    [Theory]
    [MemberData(nameof(SupportedCapabilities))]
    public void SupportedCapability_HasExecutableParserContract(string key, string contractCase)
    {
        bool registered = ContractCases.TryGetValue(contractCase, out Action? assertion);

        Assert.True(registered, $"Supported capability '{key}' has no parser contract registered as '{contractCase}'.");
        assertion!();
    }

    [Theory]
    [MemberData(nameof(SupportedCapabilities))]
    public void SupportedCapability_IsEquivalentAcrossEveryInputSplit(string key, string contractCase)
    {
        Assert.True(
            ContractSequences.TryGetValue(contractCase, out string? sequence),
            $"Supported capability '{key}' has no split-input sequence registered as '{contractCase}'.");

        (int expectedRow, int expectedCol) = ProcessAtEverySplit(sequence!, splitAt: null);
        for (int splitAt = 1; splitAt < sequence!.Length; splitAt++)
        {
            (int actualRow, int actualCol) = ProcessAtEverySplit(sequence, splitAt);
            Assert.Equal(expectedRow, actualRow);
            Assert.Equal(expectedCol, actualCol);
        }
    }

    private static void AssertCursorNextLine()
    {
        AssertPosition("\x1b[E", expectedRow: 4, expectedCol: 0);
        AssertPosition("\x1b[0E", expectedRow: 4, expectedCol: 0);
        AssertPosition("\x1b[2E", expectedRow: 5, expectedCol: 0);
        AssertPosition("\x1b[2;3E", expectedRow: 5, expectedCol: 0);
        AssertPosition("\x1b[2:3E", expectedRow: 5, expectedCol: 0);
        AssertPosition("\x1b[999E", expectedRow: 7, expectedCol: 0);
        AssertIgnored("\x1b[?2E");
        AssertIgnored("\x1b[2$E");
    }

    private static void AssertCursorPreviousLine()
    {
        AssertPosition("\x1b[F", expectedRow: 2, expectedCol: 0);
        AssertPosition("\x1b[0F", expectedRow: 2, expectedCol: 0);
        AssertPosition("\x1b[2F", expectedRow: 1, expectedCol: 0);
        AssertPosition("\x1b[2;3F", expectedRow: 1, expectedCol: 0);
        AssertPosition("\x1b[2:3F", expectedRow: 1, expectedCol: 0);
        AssertPosition("\x1b[999F", expectedRow: 0, expectedCol: 0);
        AssertIgnored("\x1b[?2F");
        AssertIgnored("\x1b[2$F");
    }

    private static void AssertCursorHorizontalAbsolute()
    {
        AssertPosition("\x1b[G", expectedRow: 3, expectedCol: 0);
        AssertPosition("\x1b[0G", expectedRow: 3, expectedCol: 0);
        AssertPosition("\x1b[4G", expectedRow: 3, expectedCol: 3);
        AssertPosition("\x1b[2;3G", expectedRow: 3, expectedCol: 1);
        AssertPosition("\x1b[2:3G", expectedRow: 3, expectedCol: 1);
        AssertPosition("\x1b[999G", expectedRow: 3, expectedCol: 11);

        // #274: these two used to assert that CHA runs for the '?' leader and the '$'
        // intermediate. That was a transcription of what the parser happened to do - CHA had no
        // leader guard - rather than a decision, and it contradicted the identical shapes three
        // lines up in AssertCursorNextLine and AssertCursorPreviousLine, whose finals had already
        // been guarded by #264. A leader or an intermediate makes it a different sequence, so it
        // is ignored, exactly as CSI ? 2 E and CSI 2 $ F are.
        AssertIgnored("\x1b[?2G");
        AssertIgnored("\x1b[2$G");
    }

    private static void AssertPosition(string sequence, int expectedRow, int expectedCol)
    {
        var buffer = new TerminalBuffer(cols: 12, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[4;6H");

        parser.Process(sequence);

        Assert.Equal(expectedRow, buffer.CursorRow);
        Assert.Equal(expectedCol, buffer.CursorCol);
    }

    private static void AssertIgnored(string sequence)
    {
        var buffer = new TerminalBuffer(cols: 12, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[4;6H");

        parser.Process(sequence);

        Assert.Equal(3, buffer.CursorRow);
        Assert.Equal(5, buffer.CursorCol);
    }

    private static (int Row, int Col) ProcessAtEverySplit(string sequence, int? splitAt)
    {
        var buffer = new TerminalBuffer(cols: 12, rows: 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[4;6H");

        if (splitAt is int index)
        {
            parser.Process(sequence[..index]);
            parser.Process(sequence[index..]);
        }
        else
        {
            parser.Process(sequence);
        }

        return (buffer.CursorRow, buffer.CursorCol);
    }
}
