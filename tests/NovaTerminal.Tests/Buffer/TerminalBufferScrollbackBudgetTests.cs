using NovaTerminal.Core;

namespace NovaTerminal.Tests.Buffer;

public sealed class TerminalBufferScrollbackBudgetTests
{
    [Fact]
    public void MaxHistory_WhenUpdated_RecomputesScrollbackBudget()
    {
        var buffer = new TerminalBuffer(80, 24);

        buffer.MaxHistory = 1000;

        long expectedBytes = 80L * 1000 * 12;

        Assert.Equal(expectedBytes, buffer.MaxScrollbackBytes);
        Assert.Equal(expectedBytes, buffer.Scrollback.MaxScrollbackBytes);
    }

    [Fact]
    public void Scrollback_WhenFlooded_DoesNotRetainMoreRowsThanMaxHistoryBudget()
    {
        var buffer = new TerminalBuffer(80, 24)
        {
            MaxHistory = 1000
        };

        for (int i = 0; i < 10000; i++)
        {
            buffer.WriteContent(new string('X', 80));
            buffer.WriteChar('\n');
        }

        TerminalMemoryMetrics metrics = buffer.GetMemoryMetrics();
        long expectedBudget = 80L * 1000 * 12;

        Assert.True(buffer.Scrollback.Count <= 1000, $"Expected <= 1000 scrollback rows, got {buffer.Scrollback.Count}.");
        Assert.True(metrics.ScrollbackBytes <= expectedBudget + (80L * 64 * 12), $"Expected scrollback bytes near budget, got {metrics.ScrollbackBytes}.");
    }

    [Fact]
    public void Resize_WhenWidthChanges_RecomputesScrollbackBudget()
    {
        var buffer = new TerminalBuffer(80, 24)
        {
            MaxHistory = 1000
        };

        buffer.Resize(120, 24);

        long expectedBytes = 120L * 1000 * 12;

        Assert.Equal(expectedBytes, buffer.MaxScrollbackBytes);
        Assert.Equal(expectedBytes, buffer.Scrollback.MaxScrollbackBytes);
    }
}
