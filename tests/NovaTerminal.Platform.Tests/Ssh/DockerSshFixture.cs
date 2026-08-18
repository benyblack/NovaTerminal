using System.Diagnostics;
using System.Net.Sockets;

namespace NovaTerminal.Platform.Tests.Ssh;

/// <summary>Which of the image's build-time test keys to use.</summary>
internal enum NativeSshTestKey
{
    /// <summary>Unencrypted; authenticates without any passphrase prompt.</summary>
    Plain = 0,

    /// <summary>Encrypted with <see cref="DockerSshFixture.PrivateKeyPassphrase"/>.</summary>
    PassphraseProtected = 1
}

internal sealed class DockerSshFixture : IAsyncDisposable
{
    // v3 turns on public-key and keyboard-interactive auth, which v2 refused outright. Bumping the
    // tag matters: EnsureImageBuiltAsync reuses any already-built image with this name, so a stale v2
    // would silently serve the new tests a server that rejects the methods they are testing.
    private const string ImageTag = "novaterm-native-ssh-e2e:v3";
    private const int EchoServicePortValue = 9001;
    private string _containerName = string.Empty;
    private bool _started;

    private DockerSshFixture(int port)
    {
        Port = port;
    }

    public string Host => "127.0.0.1";
    public int Port { get; }
    public string UserName => "nova";
    public string Password => "nova-pass";

    /// <summary>
    /// A user the server allows ONLY via keyboard-interactive. Password and public-key auth are
    /// refused for it, so a client cannot accidentally satisfy the server by another route and leave
    /// the challenge-response path untested.
    /// </summary>
    public string KeyboardInteractiveUserName => "kbdnova";

    public string KeyboardInteractivePassword => "kbd-pass";

    /// <summary>Passphrase for <see cref="NativeSshTestKey.PassphraseProtected"/>.</summary>
    public string PrivateKeyPassphrase => "nova-key-pass";

    /// <summary>
    /// Port of the in-container TCP echo service, as seen from the SSH server itself. Forward tests
    /// use it as the destination so they can assert bytes complete the round trip.
    /// </summary>
    public int EchoServicePort => EchoServicePortValue;

    public async Task WriteTextFileAsync(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, contents).ConfigureAwait(false);
            await RunDockerCommandAsync($"cp \"{tempFile}\" {_containerName}:{path}")
                .ConfigureAwait(false);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    public async Task<SshHostKeyInfo> GetHostKeyAsync()
    {
        string publicKey = await RunDockerCommandAsync(
            $"exec {_containerName} cat /etc/ssh/ssh_host_ed25519_key.pub")
            .ConfigureAwait(false);
        string fingerprintOutput = await RunDockerCommandAsync(
            $"exec {_containerName} ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub -E sha256")
            .ConfigureAwait(false);

        string algorithm = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        string fingerprint = fingerprintOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        return new SshHostKeyInfo(algorithm, fingerprint);
    }

    public async Task<string> ReadTextFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return await RunDockerCommandAsync($"exec {_containerName} cat {path}")
            .ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await RunDockerCommandAsync($"exec {_containerName} mkdir -p {path}")
            .ConfigureAwait(false);
    }

    public async Task SetLoginShellAsync(string shellPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellPath);

        await RunDockerCommandAsync($"exec {_containerName} usermod -s {shellPath} {UserName}")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Copies one of the image's build-time private keys to <paramref name="destinationPath"/> on the
    /// host, so a test can point <c>IdentityFilePath</c> at it. The keys are generated per image build
    /// rather than committed — see the Dockerfile for why.
    /// </summary>
    public async Task<string> CopyPrivateKeyAsync(NativeSshTestKey key, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string remotePath = key switch
        {
            NativeSshTestKey.Plain => "/novaterm-keys/id_ed25519",
            NativeSshTestKey.PassphraseProtected => "/novaterm-keys/id_ed25519_encrypted",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown test key.")
        };

        await RunDockerCommandAsync($"cp {_containerName}:{remotePath} \"{destinationPath}\"")
            .ConfigureAwait(false);
        return destinationPath;
    }

    public static async Task<DockerSshFixture> StartAsync()
    {
        await EnsureDockerAvailableAsync().ConfigureAwait(false);
        await EnsureImageBuiltAsync().ConfigureAwait(false);

        string containerName = $"novaterm-native-ssh-e2e-{Guid.NewGuid():N}";
        string runOutput = await RunDockerCommandAsync(
            $"run -d --rm --name {containerName} -p 127.0.0.1::22 {ImageTag}")
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(runOutput))
        {
            throw new InvalidOperationException("docker run did not return a container id.");
        }

        int mappedPort = await ResolveMappedPortAsync(containerName).ConfigureAwait(false);
        await WaitForPortAsync(containerName, mappedPort).ConfigureAwait(false);
        await WaitForEchoServiceAsync(containerName).ConfigureAwait(false);

        var fixture = new DockerSshFixture(mappedPort)
        {
            _started = true,
            _containerName = containerName
        };

        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started || string.IsNullOrWhiteSpace(_containerName))
        {
            return;
        }

        try
        {
            await RunDockerCommandAsync($"rm -f {_containerName}", throwOnFailure: false).ConfigureAwait(false);
        }
        finally
        {
            _started = false;
        }
    }

    private static async Task EnsureDockerAvailableAsync()
    {
        await RunDockerCommandAsync("info --format \"{{.ServerVersion}}\"").ConfigureAwait(false);
    }

    private static async Task EnsureImageBuiltAsync()
    {
        string rebuild = Environment.GetEnvironmentVariable("NOVATERM_REBUILD_DOCKER_E2E") ?? string.Empty;
        bool shouldRebuild = rebuild == "1" || string.Equals(rebuild, "true", StringComparison.OrdinalIgnoreCase);
        if (!shouldRebuild)
        {
            string inspect = await RunDockerCommandAsync($"image inspect {ImageTag}", throwOnFailure: false).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(inspect))
            {
                return;
            }
        }

        string dockerfilePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NovaTerminal.ExternalSuites", "NativeSsh", "Dockerfile"));
        string contextDir = Path.GetDirectoryName(dockerfilePath)
            ?? throw new InvalidOperationException("Unable to resolve Docker build context.");

        await RunDockerCommandAsync($"build -t {ImageTag} -f \"{dockerfilePath}\" \"{contextDir}\"").ConfigureAwait(false);
    }

    private static async Task<int> ResolveMappedPortAsync(string containerName)
    {
        string portOutput = await RunDockerCommandAsync($"port {containerName} 22/tcp").ConfigureAwait(false);
        string lastSegment = portOutput.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries)[^1];
        if (!int.TryParse(lastSegment, out int port))
        {
            throw new InvalidOperationException($"Unable to parse mapped SSH port from docker output '{portOutput}'.");
        }

        return port;
    }

    private static async Task WaitForPortAsync(string containerName, int port)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            string status = await RunDockerCommandAsync(
                $"inspect {containerName} --format \"{{{{.State.Status}}}}|{{{{.State.ExitCode}}}}|{{{{.State.Error}}}}\"",
                throwOnFailure: false).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(status) && !status.StartsWith("running|", StringComparison.Ordinal))
            {
                string logs = await RunDockerCommandAsync($"logs {containerName}", throwOnFailure: false).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Docker SSH container '{containerName}' stopped before becoming ready. State: {status}. Logs: {logs}");
            }

            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
                if (client.Connected)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        string finalStatus = await RunDockerCommandAsync(
            $"inspect {containerName} --format \"{{{{.State.Status}}}}|{{{{.State.ExitCode}}}}|{{{{.State.Error}}}}\"",
            throwOnFailure: false).ConfigureAwait(false);
        string finalLogs = await RunDockerCommandAsync($"logs {containerName}", throwOnFailure: false).ConfigureAwait(false);
        throw new TimeoutException(
            $"Docker SSH server on port {port} did not become ready within 30 seconds. State: {finalStatus}. Logs: {finalLogs}");
    }

    /// <summary>
    /// Waits for the entrypoint's echo service to accept connections. Checked at fixture start rather
    /// than inside the forwarding tests so a broken echo service reports itself as such, instead of
    /// surfacing later as a forwarding test that mysteriously reads no bytes back.
    /// </summary>
    private static async Task WaitForEchoServiceAsync(string containerName)
    {
        // socat exits non-zero when it cannot connect, so the marker only reaches stdout on success —
        // RunDockerCommandAsync collapses any failure to an empty string, which alone is ambiguous.
        string probe = $"exec {containerName} sh -c \"socat -u OPEN:/dev/null TCP:127.0.0.1:{EchoServicePortValue} && echo echo-service-ready\"";

        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            string output = await RunDockerCommandAsync(probe, throwOnFailure: false).ConfigureAwait(false);
            if (output.Contains("echo-service-ready", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        string logs = await RunDockerCommandAsync($"logs {containerName}", throwOnFailure: false).ConfigureAwait(false);
        throw new TimeoutException(
            $"Echo service on container port {EchoServicePortValue} did not accept connections within 20 seconds. Logs: {logs}");
    }

    private static async Task<string> RunDockerCommandAsync(string arguments, bool throwOnFailure = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {arguments} failed with exit code {process.ExitCode}: {stderr}{stdout}");
        }

        if (!throwOnFailure && process.ExitCode != 0)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(stdout) ? stderr.Trim() : stdout.Trim();
    }
}

internal sealed record SshHostKeyInfo(string Algorithm, string Fingerprint);
