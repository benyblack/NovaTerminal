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
/// The auth, host-key and forwarding rows that docs/native-ssh/Native_SSH_Test_Matrix.md listed as
/// "pending manual". They were manual because the v2 fixture image refused public-key and
/// keyboard-interactive auth and had nothing to forward traffic to — not because they resist
/// automation. v3 supplies both, so these run against a real sshd.
/// </summary>
public sealed class NativeSshDockerAuthE2eTests
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(30);

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task PrivateKeyAuth_WithUnencryptedKey_AuthenticatesWithoutAskingForAnything()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = CreateTempRoot("nova-native-key-plain");
        try
        {
            string keyPath = await fixture.CopyPrivateKeyAsync(
                NativeSshTestKey.Plain,
                Path.Combine(tempRoot, "id_ed25519"));

            var buffer = new TerminalBuffer(120, 30);
            var parser = new AnsiParser(buffer);
            var handler = new NativeSshTestInteractionHandler(fixture.Password);

            SshProfile profile = CreateProfile(fixture);
            profile.AuthMode = SshAuthMode.IdentityFile;
            profile.IdentityFilePath = keyPath;

            using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
            session.OnOutputReceived += parser.Process;

            await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt after key auth");

            // A usable key must satisfy the server outright: no password, no passphrase. Only the
            // host-key question is expected, because sessions never receive a known-hosts path.
            Assert.DoesNotContain(handler.RequestSnapshot(), request => request.Kind == SshInteractionKind.Password);
            Assert.DoesNotContain(handler.RequestSnapshot(), request => request.Kind == SshInteractionKind.Passphrase);
            Assert.Contains(handler.RequestSnapshot(), request => request.Kind == SshInteractionKind.UnknownHostKey);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task PrivateKeyAuth_WithEncryptedKey_PromptsForThePassphraseAndThenAuthenticates()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = CreateTempRoot("nova-native-key-encrypted");
        try
        {
            string keyPath = await fixture.CopyPrivateKeyAsync(
                NativeSshTestKey.PassphraseProtected,
                Path.Combine(tempRoot, "id_ed25519_encrypted"));

            var buffer = new TerminalBuffer(120, 30);
            var parser = new AnsiParser(buffer);
            var handler = new NativeSshTestInteractionHandler(
                fixture.Password,
                passphrase: fixture.PrivateKeyPassphrase);

            SshProfile profile = CreateProfile(fixture);
            profile.AuthMode = SshAuthMode.IdentityFile;
            profile.IdentityFilePath = keyPath;

            using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
            session.OnOutputReceived += parser.Process;

            await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt after encrypted key auth");

            // The passphrase is what unlocks the key; the password path must not be reached at all.
            Assert.Contains(handler.RequestSnapshot(), request => request.Kind == SshInteractionKind.Passphrase);
            Assert.DoesNotContain(handler.RequestSnapshot(), request => request.Kind == SshInteractionKind.Password);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task KeyboardInteractiveAuth_ForAUserThatAcceptsNothingElse_Authenticates()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.KeyboardInteractivePassword);

        SshProfile profile = CreateProfile(fixture);
        profile.User = fixture.KeyboardInteractiveUserName;

        using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt after keyboard-interactive auth");

        // The backend asks for a password unconditionally before it ever tries keyboard-interactive
        // (see `authenticate` in rusty_ssh), so a password request here is expected and not a failure.
        // What matters is that the password attempt was refused by the server and the challenge-response
        // fallback carried the session.
        Assert.Contains(handler.RequestSnapshot(), request => request.Kind == SshInteractionKind.KeyboardInteractive);
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task HostKeyPrompt_CarriesTheAlgorithmAndFingerprintTheServerActuallyHas()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        SshHostKeyInfo expected = await fixture.GetHostKeyAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);

        using var session = new NativeSshSession(CreateProfile(fixture), cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt");

        SshInteractionRequest hostKeyRequest = Assert.Single(
            handler.RequestSnapshot().Where(request => request.Kind == SshInteractionKind.UnknownHostKey));

        // Unit tests can pin the trust-store logic but not the derivation: this compares what the
        // backend reported against what ssh-keygen says the server's key is.
        Assert.Equal(expected.Algorithm, hostKeyRequest.Algorithm);
        Assert.Equal(expected.Fingerprint, hostKeyRequest.Fingerprint);
        Assert.Equal(fixture.Host, hostKeyRequest.Host);
        Assert.Equal(fixture.Port, hostKeyRequest.Port);
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task HostKeyPrompt_WhenRefused_NeverReachesTheCredentialPrompt()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password, acceptHostKeys: false);

        using var session = new NativeSshSession(CreateProfile(fixture), cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(
            () => handler.RequestSnapshot().Any(request => request.Kind == SshInteractionKind.UnknownHostKey),
            PromptTimeout,
            "host-key prompt");

        // Fail closed: verification happens during the handshake, so refusing the key must stop the
        // connection before any credential is solicited, and no shell may appear.
        await AssertStaysTrueAsync(
            () => !handler.RequestSnapshot().Any(request => request.Kind is SshInteractionKind.Password or SshInteractionKind.Passphrase)
                  && !SnapshotContains(buffer, "nova$"),
            TimeSpan.FromSeconds(5),
            "no credential prompt and no shell after refusing the host key");
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task LocalForward_CarriesRealBytesToTheEchoService()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        int localPort = GetFreeLocalPort();
        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);

        SshProfile profile = CreateProfile(fixture);
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Local,
            BindAddress = "127.0.0.1",
            SourcePort = localPort,
            // Destination is resolved by the SSH server, so this is the container's own loopback.
            DestinationHost = "127.0.0.1",
            DestinationPort = fixture.EchoServicePort
        });

        using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        // Wait for the shell first: the listener binds during construction, but a channel cannot open
        // until the session is established and authenticated.
        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt before forwarding");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort);

        const string payload = "local-forward-round-trip";
        string echoed = await EchoRoundTripAsync(client.GetStream(), payload, TimeSpan.FromSeconds(30));

        Assert.Equal(payload, echoed);
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task DynamicForward_CarriesRealBytesThroughSocks5ToTheEchoService()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        int localPort = GetFreeLocalPort();
        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);

        SshProfile profile = CreateProfile(fixture);
        profile.Forwards.Add(new PortForward
        {
            Kind = PortForwardKind.Dynamic,
            BindAddress = "127.0.0.1",
            SourcePort = localPort
        });

        using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt before forwarding");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort);
        NetworkStream stream = client.GetStream();

        await Socks5ConnectAsync(stream, "127.0.0.1", fixture.EchoServicePort, TimeSpan.FromSeconds(30));

        const string payload = "socks5-round-trip";
        string echoed = await EchoRoundTripAsync(stream, payload, TimeSpan.FromSeconds(30));

        Assert.Equal(payload, echoed);
    }

    private static string CreateTempRoot(string prefix)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static SshProfile CreateProfile(DockerSshFixture fixture)
    {
        return new SshProfile
        {
            Id = Guid.NewGuid(),
            Name = "Docker Native SSH (auth)",
            BackendKind = SshBackendKind.Native,
            Host = fixture.Host,
            User = fixture.UserName,
            Port = fixture.Port
        };
    }
}
