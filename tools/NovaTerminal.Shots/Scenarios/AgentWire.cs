using System.Text.Json;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;
using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The one wire call any scenario that drives the agent host needs: a serialized <c>sendInput</c>
/// frame handed to AgentHostService's line-level entry point, exactly as NovaTerminal.McpServer's
/// send_input tool would send it. Extracted out of AgentSessionScenario (its original, and still
/// its heaviest, user) so that a second scenario needing the same wire call - TabsVerticalScenario,
/// which needs one pane to show real agent activity rather than a staged marker - does not have to
/// re-derive the request envelope and id bookkeeping.
/// </summary>
internal static class AgentWire
{
    private static long _requestId;

    /// <summary>
    /// Sends <paramref name="text"/> to <paramref name="paneId"/> through the agent host's
    /// sendInput method, submitting it with Enter unless <paramref name="submit"/> is false.
    /// Returns the raw response, error included, so a caller that expects a refusal (an
    /// unregistered pane, act disabled) can inspect it instead of having the call throw.
    /// </summary>
    public static async Task<AgentHostResponse> SendInputAsync(Guid paneId, string text, bool submit = true)
    {
        var request = new AgentHostRequest
        {
            Version = AgentHostProtocol.Version,
            Id = Interlocked.Increment(ref _requestId),
            Method = AgentHostProtocol.Methods.SendInput,
            Params = JsonSerializer.SerializeToElement(
                new SendInputParams { PaneId = paneId, Text = text, Submit = submit },
                AgentHostJsonContext.Default.SendInputParams)
        };

        string line = JsonSerializer.Serialize(request, AgentHostJsonContext.Default.AgentHostRequest);

        return await AgentHostService.Instance.HandleRequestLineAsync(line, CancellationToken.None);
    }

    /// <summary>
    /// The success-only shape most scenarios actually want: deliver <paramref name="command"/> to
    /// <paramref name="pane"/> and throw if the host refused it, rather than every caller
    /// re-checking <c>response.Error</c> for the delivery it expects to succeed.
    /// </summary>
    public static async Task DeliverAsync(TerminalPane pane, string command)
    {
        AgentHostResponse response = await SendInputAsync(pane.PaneId, command);

        if (response.Error is not null)
        {
            throw new InvalidOperationException(
                $"agent sendInput was rejected: {response.Error.Code} {response.Error.Message}. " +
                "Act is probably still disabled, or the pane is not registered.");
        }
    }
}
