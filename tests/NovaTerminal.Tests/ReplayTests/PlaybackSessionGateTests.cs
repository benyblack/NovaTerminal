using NovaTerminal.UI.Replay;

namespace NovaTerminal.Tests.ReplayTests;

public sealed class PlaybackSessionGateTests
{
    [Fact]
    public void InvalidateCurrentSession_MarksPreviousSessionAsStale()
    {
        var gate = new PlaybackSessionGate();

        int first = gate.BeginSession();
        Assert.True(gate.IsCurrent(first));

        gate.InvalidateCurrentSession();

        Assert.False(gate.IsCurrent(first));
    }

    [Fact]
    public void NewerSessionRemainsCurrentAfterOlderSessionInvalidation()
    {
        var gate = new PlaybackSessionGate();

        int first = gate.BeginSession();
        gate.InvalidateCurrentSession();
        int second = gate.BeginSession();

        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
    }
}
