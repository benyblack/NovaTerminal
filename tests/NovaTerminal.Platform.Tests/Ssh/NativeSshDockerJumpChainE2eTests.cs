using System.Net;
using System.Net.Sockets;
using NovaTerminal.Platform.Ssh.Interactions;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Sessions;
using NovaTerminal.Platform.Tests.Infra;
using NovaTerminal.VT;
using static NovaTerminal.Platform.Tests.Ssh.NativeSshDockerTestWaits;

namespace NovaTerminal.Platform.Tests.Ssh;

/// <summary>
/// The jump-host rows of docs/native-ssh/Native_SSH_Test_Matrix.md, against a real sshd.
///
/// Every hop dials the fixture container back into itself: the first hop is the mapped host port,
/// every later address is <see cref="DockerSshFixture.InContainerSshPort"/>, which the hop's sshd
/// resolves on its own loopback. That makes each hop a genuinely separate SSH session — its own
/// TCP-or-tunnel transport, handshake, host-key check and authentication — without the
/// multi-container network fixture the matrix used to say these rows were waiting for.
/// </summary>
public sealed class NativeSshDockerJumpChainE2eTests
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(30);

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task JumpHost_OneHop_ReachesAShellOnTheTarget()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);

        SshProfile profile = CreateTunnelledProfile(fixture, hopCount: 1);

        using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt through one jump hop");

        // Two distinct servers were verified from the client's point of view: the hop on the mapped
        // port and the target on the container-internal one. Both must have gone through the
        // host-key prompt — a single prompt would mean the "tunnel" was really a direct connect.
        Assert.Equal(
            2,
            handler.RequestSnapshot().Count(request => request.Kind == SshInteractionKind.UnknownHostKey));
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task JumpChain_TwoHops_ReachesALiveShellOnTheTarget()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);

        SshProfile profile = CreateTunnelledProfile(fixture, hopCount: 2);

        using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt through a two-hop chain");

        // A prompt alone could be buffered output from a session that has since wedged; make the
        // innermost shell compute something so the assertion spans the whole nested pipe both ways.
        session.SendInput("printf 'chain-%s\\n' \"$((6 * 7))\"\n");
        await WaitUntilAsync(() => SnapshotContains(buffer, "chain-42"), PromptTimeout, "command output through the chain");

        // Three sessions, three handshakes, three host-key verdicts.
        Assert.Equal(
            3,
            handler.RequestSnapshot().Count(request => request.Kind == SshInteractionKind.UnknownHostKey));
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task DynamicForward_ThroughAJumpHop_CarriesRealBytesToTheEchoService()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        int localPort = GetFreeLocalPort();
        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);

        SshProfile profile = CreateTunnelledProfile(fixture, hopCount: 1);
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Dynamic,
            BindAddress = "127.0.0.1",
            SourcePort = localPort
        });

        using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt before forwarding through the hop");

        // The forward's direct-tcpip channels ride the target session, which itself rides the
        // hop's tunnel — so these bytes cross two nested SSH transports each way.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort);
        NetworkStream stream = client.GetStream();

        await Socks5ConnectAsync(stream, "127.0.0.1", fixture.EchoServicePort, TimeSpan.FromSeconds(30));

        const string payload = "socks5-through-a-jump-hop";
        string echoed = await EchoRoundTripAsync(stream, payload, TimeSpan.FromSeconds(30));

        Assert.Equal(payload, echoed);
    }

    /// <summary>
    /// A profile whose first hop is the container's mapped host port and whose remaining hops and
    /// target are the container-internal sshd port, resolved by each hop on its own loopback.
    /// Only the first hop is reachable from the host at all — reaching the prompt proves the
    /// tunnels, not just the addresses.
    /// </summary>
    private static SshProfile CreateTunnelledProfile(DockerSshFixture fixture, int hopCount)
    {
        var profile = new SshProfile
        {
            Id = Guid.NewGuid(),
            Name = $"Docker Native SSH ({hopCount}-hop jump)",
            BackendKind = SshBackendKind.Native,
            Host = "127.0.0.1",
            Port = fixture.InContainerSshPort,
            User = fixture.UserName
        };

        profile.JumpHops.Add(new SshJumpHop
        {
            Host = fixture.Host,
            User = fixture.UserName,
            Port = fixture.Port
        });
        for (int i = 1; i < hopCount; i++)
        {
            profile.JumpHops.Add(new SshJumpHop
            {
                Host = "127.0.0.1",
                User = fixture.UserName,
                Port = fixture.InContainerSshPort
            });
        }

        return profile;
    }
}
