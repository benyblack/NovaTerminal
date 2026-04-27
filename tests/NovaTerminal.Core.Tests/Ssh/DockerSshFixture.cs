using System.Diagnostics;
using System.Net.Sockets;

namespace NovaTerminal.Core.Tests.Ssh;

internal sealed class DockerSshFixture : IAsyncDisposable
{
    private const string ImageTag = "novaterm-native-ssh-e2e:local";
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
