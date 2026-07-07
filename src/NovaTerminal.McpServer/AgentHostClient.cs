using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NovaTerminal.AgentHost.Contracts;

namespace NovaTerminal.McpServer;

/// <summary>
/// Client side of the agent-host observe channel
/// (docs/agent-host/DIRECTION.md, milestone A1). Discovers the running app's
/// endpoint via the descriptor file next to settings.json and speaks the
/// newline-delimited JSON frame protocol from NovaTerminal.AgentHost.Contracts.
///
/// Unavailability is a normal state, not an exception: when NovaTerminal isn't
/// running or the user has not enabled "Agent access (observe)", callers get a
/// human-readable reason to surface verbatim to the agent.
/// </summary>
public sealed class AgentHostClient
{
    /// <summary>Fixed guidance surfaced when no live endpoint is reachable.</summary>
    public const string UnavailableMessage =
        "Live session access is unavailable: NovaTerminal is not running with Agent Access enabled. " +
        "Start NovaTerminal and enable Settings → Agent access (observe), then retry. " +
        "This surface is observe-only: agents can read sessions but cannot type or open them.";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RoundTripTimeout = TimeSpan.FromSeconds(10);

    private readonly string _discoveryFilePath;
    private long _nextId;

    public AgentHostClient(string? discoveryFilePathOverride = null)
    {
        _discoveryFilePath = discoveryFilePathOverride ?? AgentHostDiscovery.GetDefaultDiscoveryFilePath();
    }

    /// <summary>Outcome of a call: either a protocol response, or a reason the endpoint is unreachable.</summary>
    public sealed record CallOutcome(AgentHostResponse? Response, string? UnavailableReason)
    {
        public bool Available => Response != null;
    }

    public async Task<CallOutcome> CallAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var descriptor = TryReadLiveDescriptor();
        if (descriptor == null)
        {
            return new CallOutcome(null, UnavailableMessage);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RoundTripTimeout);

            await using var stream = await ConnectAsync(descriptor.Endpoint, timeoutCts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var request = new AgentHostRequest
            {
                Version = AgentHostProtocol.Version,
                Id = Interlocked.Increment(ref _nextId),
                Method = method,
                Params = parameters,
            };
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(request, AgentHostJsonContext.Default.AgentHostRequest)
                    .AsMemory(),
                timeoutCts.Token).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            if (line == null)
            {
                return new CallOutcome(null, UnavailableMessage);
            }

            var response = JsonSerializer.Deserialize(line, AgentHostJsonContext.Default.AgentHostResponse);
            return response == null
                ? new CallOutcome(null, UnavailableMessage)
                : new CallOutcome(response, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CallOutcome(null, "Live session access timed out talking to NovaTerminal. The app may be busy; retry.");
        }
        catch (Exception)
        {
            // Connection refused / broken pipe: the endpoint died between the
            // descriptor read and the connect — same user remedy as unavailable.
            return new CallOutcome(null, UnavailableMessage);
        }
    }

    private EndpointDescriptor? TryReadLiveDescriptor()
    {
        try
        {
            if (!File.Exists(_discoveryFilePath)) return null;
            var text = File.ReadAllText(_discoveryFilePath);
            if (string.IsNullOrWhiteSpace(text)) return null; // truncated == retired

            var descriptor = JsonSerializer.Deserialize(text, AgentHostJsonContext.Default.EndpointDescriptor);
            if (descriptor == null || descriptor.Version != AgentHostProtocol.Version) return null;

            // Stale-descriptor guard, mirroring the app side: the advertised
            // pid must still be alive.
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(descriptor.Pid);
                if (process.HasExited) return null;
            }
            catch (ArgumentException)
            {
                return null;
            }

            return descriptor;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Stream> ConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(ConnectTimeout);
                await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), connectCts.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
