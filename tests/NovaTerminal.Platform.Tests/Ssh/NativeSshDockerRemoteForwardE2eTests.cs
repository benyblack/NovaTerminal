using System.Net;
using System.Net.Sockets;
using System.Text;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Sessions;
using NovaTerminal.Platform.Tests.Infra;
using NovaTerminal.VT;
using static NovaTerminal.Platform.Tests.Ssh.NativeSshDockerTestWaits;

namespace NovaTerminal.Platform.Tests.Ssh;

/// <summary>
/// The remote-forward row of docs/native-ssh/Native_SSH_Test_Matrix.md, against a real sshd.
///
/// Direction matters here: the listener lives on the SERVER, and the connection travels toward
/// the client. The fixture container's own shell session drives it — socat inside the container
/// dials the remote listener, the tunnel carries the bytes to this test, and a local destination
/// server answers with a transformed payload. Transformed on purpose: the dialling command is
/// echoed into the terminal, so asserting on the raw payload would match the echo of what was
/// typed; the transformed reply can only have come back through the tunnel.
/// </summary>
public sealed class NativeSshDockerRemoteForwardE2eTests
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The port the remote listener binds inside the container. Fixed rather than probed: each
    /// test container has its own network namespace, so there is nothing to collide with.
    /// </summary>
    private const int RemoteListenPort = 18080;

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task RemoteForward_CarriesRealBytesFromTheServerToALocalDestination()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        // The local destination the tunnel must reach: reads one request, answers with a
        // transformed copy, and closes its send side so socat sees end-of-stream and exits.
        var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        int destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        _ = Task.Run(() => AnswerOneConnectionAsync(destination));

        try
        {
            var buffer = new TerminalBuffer(120, 30);
            var parser = new AnsiParser(buffer);
            var handler = new NativeSshTestInteractionHandler(fixture.Password);

            var profile = new SshProfile
            {
                Id = Guid.NewGuid(),
                Name = "Docker Native SSH (remote forward)",
                BackendKind = SshBackendKind.Native,
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port
            };
            profile.Forwards.Add(new PortForward
            {
                Kind = PortForwardKind.Remote,
                SourcePort = RemoteListenPort,
                DestinationHost = "127.0.0.1",
                DestinationPort = destinationPort
            });

            using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
            session.OnOutputReceived += parser.Process;

            await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt before remote forwarding");

            // The tcpip-forward request races the prompt (it is made once the session is
            // established, on its own task), so the dial retries until the listener exists.
            // socat prints whatever comes back through the tunnel into this very terminal.
            session.SendInput(
                $"for i in $(seq 1 50); do printf 'remote-ping' | socat -T 10 - TCP:127.0.0.1:{RemoteListenPort} && break; sleep 0.2; done\n");

            await WaitUntilAsync(
                () => SnapshotContains(buffer, "reply:remote-ping"),
                TimeSpan.FromSeconds(30),
                "the transformed reply travelling server -> tunnel -> local destination -> back");
        }
        finally
        {
            destination.Stop();
        }
    }

    private static async Task AnswerOneConnectionAsync(TcpListener destination)
    {
        try
        {
            using TcpClient client = await destination.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();

            // Read the whole known payload, not whatever the first read returns — a split
            // "remote-p"/"ing" would otherwise produce a truncated reply and a flaky assertion.
            byte[] request = new byte[Encoding.ASCII.GetByteCount("remote-ping")];
            int offset = 0;
            while (offset < request.Length)
            {
                int read = await stream.ReadAsync(request.AsMemory(offset));
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            byte[] reply = Encoding.ASCII.GetBytes("reply:" + Encoding.ASCII.GetString(request, 0, offset));
            await stream.WriteAsync(reply);
            await stream.FlushAsync();
            client.Client.Shutdown(SocketShutdown.Send);

            // Give socat a moment to drain before the socket is torn down with the using.
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // Failures surface as the missing reply in the terminal assertion; nothing useful to
            // do with them here.
        }
    }
}
