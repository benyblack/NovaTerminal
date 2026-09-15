using System.Threading;

namespace NovaTerminal.UI.Replay;

internal sealed class PlaybackSessionGate
{
    private int _currentSessionId;

    public int BeginSession()
    {
        return Interlocked.Increment(ref _currentSessionId);
    }

    public void InvalidateCurrentSession()
    {
        Interlocked.Increment(ref _currentSessionId);
    }

    public bool IsCurrent(int sessionId)
    {
        return Volatile.Read(ref _currentSessionId) == sessionId;
    }
}
