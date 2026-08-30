using NovaTerminal.VtContract;

namespace NovaTerminal.VT.Tests;

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
        AssertPosition("\x1b[999E", expectedRow: 7, expectedCol: 0);
        AssertIgnored("\x1b[?2E");
        AssertIgnored("\x1b[2$E");
    }

    private static void AssertCursorPreviousLine()
    {
        AssertPosition("\x1b[F", expectedRow: 2, expectedCol: 0);
        AssertPosition("\x1b[0F", expectedRow: 2, expectedCol: 0);
        AssertPosition("\x1b[2F", expectedRow: 1, expectedCol: 0);
        AssertPosition("\x1b[999F", expectedRow: 0, expectedCol: 0);
        AssertIgnored("\x1b[?2F");
        AssertIgnored("\x1b[2$F");
    }

    private static void AssertCursorHorizontalAbsolute()
    {
        AssertPosition("\x1b[G", expectedRow: 3, expectedCol: 0);
        AssertPosition("\x1b[0G", expectedRow: 3, expectedCol: 0);
        AssertPosition("\x1b[4G", expectedRow: 3, expectedCol: 3);
        AssertPosition("\x1b[999G", expectedRow: 3, expectedCol: 11);
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
