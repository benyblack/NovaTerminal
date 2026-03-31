namespace NovaTerminal.Core.Ssh.Native;

public enum NativeSshFailureKind
{
    Unknown = 0,
    Timeout = 1,
    Authentication = 2,
    HostKeyMismatch = 3,
    ChannelOpen = 4,
    ForwardBind = 5,
    RemoteDisconnect = 6
}

public sealed class NativeSshFailure
{
    public NativeSshFailure(NativeSshFailureKind kind, string message)
    {
        Kind = kind;
        Message = message ?? string.Empty;
    }

    public NativeSshFailureKind Kind { get; }
    public string Message { get; }
}

public static class NativeSshFailureClassifier
{
    public static NativeSshFailure Classify(string? message)
    {
        string text = message?.Trim() ?? string.Empty;
        string lower = text.ToLowerInvariant();

        NativeSshFailureKind kind = lower switch
        {
            _ when lower.Contains("timed out", StringComparison.Ordinal) || lower.Contains("timeout", StringComparison.Ordinal)
                => NativeSshFailureKind.Timeout,
            _ when lower.Contains("authentication failed", StringComparison.Ordinal) || lower.Contains("prompt canceled", StringComparison.Ordinal)
                => NativeSshFailureKind.Authentication,
            _ when lower.Contains("host key", StringComparison.Ordinal) && (lower.Contains("changed", StringComparison.Ordinal) || lower.Contains("mismatch", StringComparison.Ordinal))
                => NativeSshFailureKind.HostKeyMismatch,
            _ when lower.Contains("channelopenfailure", StringComparison.Ordinal) || lower.Contains("channel open", StringComparison.Ordinal)
                => NativeSshFailureKind.ChannelOpen,
            _ when lower.Contains("bind local forward", StringComparison.Ordinal) || lower.Contains("failed to bind local forward", StringComparison.Ordinal)
                => NativeSshFailureKind.ForwardBind,
            _ when lower.Contains("remote disconnect", StringComparison.Ordinal) || lower.Contains("disconnect", StringComparison.Ordinal)
                => NativeSshFailureKind.RemoteDisconnect,
            _ => NativeSshFailureKind.Unknown
        };

        return new NativeSshFailure(kind, text);
    }
}
