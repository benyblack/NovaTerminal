using System;
using System.Linq;
using System.Threading.Tasks;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>Cursor, eviction, and long-poll semantics of the A2 event ring.</summary>
public class AgentEventRingTests
{
    private static long Append(AgentEventRing ring, string type = AgentHostProtocol.EventTypes.StatusChanged)
        => ring.Append(Guid.NewGuid(), type, AgentHostProtocol.StatusKinds.Running, DateTimeOffset.UtcNow);

    [Fact]
    public void Sequences_are_monotonic_and_cursor_reads_only_newer_events()
    {
        var ring = new AgentEventRing();
        var first = Append(ring);
        var second = Append(ring);
        Assert.Equal(first + 1, second);

        var all = ring.ReadSince(0);
        Assert.Equal(2, all.Events.Length);
        Assert.Equal(second, all.NextSeq);

        var newer = ring.ReadSince(first);
        Assert.Equal(second, Assert.Single(newer.Events).Seq);

        var none = ring.ReadSince(second);
        Assert.Empty(none.Events);
        Assert.Equal(second, none.NextSeq); // cursor keeps its position
    }

    [Fact]
    public void Eviction_is_detectable_via_oldestSeq()
    {
        var ring = new AgentEventRing(capacity: 4);
        for (var i = 0; i < 6; i++) Append(ring);

        var result = ring.ReadSince(0);
        Assert.Equal(4, result.Events.Length);
        Assert.Equal(3, result.OldestSeq); // seqs 1–2 evicted
        Assert.Equal(6, result.NextSeq);

        // A client whose cursor predates oldestSeq-1 knows it missed events.
        Assert.True(0 + 1 < result.OldestSeq);
    }

    [Fact]
    public async Task Long_poll_wakes_on_append()
    {
        var ring = new AgentEventRing();
        var wait = ring.WaitSinceAsync(0, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        var seq = Append(ring);

        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(seq, Assert.Single(result.Events).Seq);
    }

    [Fact]
    public async Task Long_poll_returns_empty_after_timeout()
    {
        var ring = new AgentEventRing();
        var result = await ring.WaitSinceAsync(0, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.Empty(result.Events);
        Assert.Equal(0, result.NextSeq);
        Assert.Equal(0, result.OldestSeq);
    }

    [Fact]
    public async Task Long_poll_returns_immediately_when_events_already_exist()
    {
        var ring = new AgentEventRing();
        Append(ring);

        var result = await ring.WaitSinceAsync(0, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(result.Events);
    }
}
