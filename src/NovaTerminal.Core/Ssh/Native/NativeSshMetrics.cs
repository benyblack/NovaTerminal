namespace NovaTerminal.Core.Ssh.Native;

public sealed class NativeSshMetrics
{
    private readonly DateTimeOffset _connectStartedUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _connectedUtc;
    private DateTimeOffset? _hostKeyPromptStartedUtc;
    private DateTimeOffset? _authPromptStartedUtc;
    private DateTimeOffset? _firstOutputUtc;

    public TimeSpan? ConnectLatency { get; private set; }
    public TimeSpan? HostKeyDuration { get; private set; }
    public TimeSpan? AuthenticationDuration { get; private set; }
    public TimeSpan? TimeToFirstOutput { get; private set; }
    public string DisconnectReason { get; private set; } = string.Empty;
    public List<string> ForwardSetupResults { get; } = [];

    public void MarkConnected()
    {
        _connectedUtc = DateTimeOffset.UtcNow;
        ConnectLatency ??= _connectedUtc.Value - _connectStartedUtc;
    }

    public void MarkHostKeyPromptStarted()
    {
        _hostKeyPromptStartedUtc ??= DateTimeOffset.UtcNow;
    }

    public void MarkHostKeyPromptCompleted()
    {
        if (_hostKeyPromptStartedUtc.HasValue)
        {
            HostKeyDuration = DateTimeOffset.UtcNow - _hostKeyPromptStartedUtc.Value;
            _hostKeyPromptStartedUtc = null;
        }
    }

    public void MarkAuthenticationPromptStarted()
    {
        _authPromptStartedUtc ??= DateTimeOffset.UtcNow;
    }

    public void MarkAuthenticationPromptCompleted()
    {
        if (_authPromptStartedUtc.HasValue)
        {
            AuthenticationDuration = DateTimeOffset.UtcNow - _authPromptStartedUtc.Value;
            _authPromptStartedUtc = null;
        }
    }

    public void MarkFirstOutput()
    {
        if (_firstOutputUtc.HasValue)
        {
            return;
        }

        _firstOutputUtc = DateTimeOffset.UtcNow;
        TimeToFirstOutput = _firstOutputUtc.Value - _connectStartedUtc;
    }

    public void RecordForwardSetup(string result)
    {
        if (!string.IsNullOrWhiteSpace(result))
        {
            ForwardSetupResults.Add(result.Trim());
        }
    }

    public void MarkDisconnected(string? reason)
    {
        DisconnectReason = reason?.Trim() ?? string.Empty;
    }
}
