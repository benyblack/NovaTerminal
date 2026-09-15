using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NovaTerminal.Platform.Ssh.Interactions;
using NovaTerminal.Platform.Ssh.Models;
using NovaTerminal.Platform.Ssh.Sessions;
using NovaTerminal.Platform.Tests.Infra;
using NovaTerminal.VT;
using static NovaTerminal.Platform.Tests.Ssh.NativeSshDockerTestWaits;

namespace NovaTerminal.Platform.Tests.Ssh;

/// <summary>
/// The ssh-agent row of docs/native-ssh/Native_SSH_Test_Matrix.md, against a real sshd and a real
/// ssh-agent: the fixture's client key is handed to an agent started by this test, never to the
/// profile — so the only way the session can authenticate without a password prompt is by the
/// backend discovering the agent and having it sign.
/// </summary>
public sealed class NativeSshDockerAgentAuthE2eTests
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(30);

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task AgentAuth_WithTheKeyHeldOnlyByTheAgent_AuthenticatesWithoutAskingForAnything()
    {
        // The agent plumbing here is Unix-shaped: libc setenv, Unix file modes, ssh-agent over a
        // Unix socket. A Windows machine with Docker can run the rest of the E2E suite; this
        // test steps aside there rather than failing on the first P/Invoke.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        SshAgentHandle? agent = null;
        try
        {
            string keyPath = await fixture.CopyPrivateKeyAsync(
                NativeSshTestKey.Plain,
                Path.Combine(tempRoot, "id_ed25519"));

            // ssh-add refuses keys it considers exposed, and docker cp does not promise the
            // container's 600 survives the copy.
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            agent = await SshAgentHandle.StartAsync(keyPath);

            // The native library discovers the agent through getenv("SSH_AUTH_SOCK"), and
            // Environment.SetEnvironmentVariable does not reach the native environment on
            // .NET/Linux — hence libc setenv. Process-global, so a concurrently running E2E
            // test's connect may offer this key too; every container generates its own keys,
            // so the other server refuses it and that connect falls through unchanged.
            SetNativeEnvironmentVariable("SSH_AUTH_SOCK", agent.SocketPath);

            var buffer = new TerminalBuffer(120, 30);
            var parser = new AnsiParser(buffer);
            var handler = new NativeSshTestInteractionHandler(fixture.Password);

            var profile = new SshProfile
            {
                Id = Guid.NewGuid(),
                Name = "Docker Native SSH (agent auth)",
                BackendKind = SshBackendKind.Native,
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                AuthMode = SshAuthMode.Agent
            };

            using var session = new NativeSshSession(profile, cols: 120, rows: 30, interactionHandler: handler);
            session.OnOutputReceived += parser.Process;

            await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), PromptTimeout, "shell prompt after agent auth");

            // The agent must satisfy the server outright. The handler would have answered a
            // password prompt correctly, so seeing none is what proves the agent path carried
            // the session; only the host-key question is expected.
            Assert.DoesNotContain(handler.RequestSnapshot(), request => request.Kind == SshInteractionKind.Password);
            Assert.DoesNotContain(handler.RequestSnapshot(), request => request.Kind == SshInteractionKind.Passphrase);
            Assert.Contains(handler.RequestSnapshot(), request => request.Kind == SshInteractionKind.UnknownHostKey);
        }
        finally
        {
            UnsetNativeEnvironmentVariable("SSH_AUTH_SOCK");
            if (agent != null)
            {
                await agent.DisposeAsync();
            }

            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    [DllImport("libc", SetLastError = true)]
    private static extern int unsetenv(string name);

    private static void SetNativeEnvironmentVariable(string name, string value)
    {
        if (setenv(name, value, 1) != 0)
        {
            throw new InvalidOperationException($"setenv({name}) failed.");
        }
    }

    private static void UnsetNativeEnvironmentVariable(string name)
    {
        _ = unsetenv(name);
    }

    /// <summary>
    /// A real ssh-agent process holding one key, torn down with the test. The socket path comes
    /// from parsing the agent's own "SSH_AUTH_SOCK=...;" startup output, the same contract eval'd
    /// by every shell that starts one.
    /// </summary>
    private sealed class SshAgentHandle : IAsyncDisposable
    {
        private readonly string _pid;

        private SshAgentHandle(string socketPath, string pid)
        {
            SocketPath = socketPath;
            _pid = pid;
        }

        public string SocketPath { get; }

        public static async Task<SshAgentHandle> StartAsync(string keyPath)
        {
            string output = await RunAsync("ssh-agent", "-s", environment: null);
            Match socket = Regex.Match(output, @"SSH_AUTH_SOCK=([^;]+);");
            Match pid = Regex.Match(output, @"SSH_AGENT_PID=(\d+);");
            if (!socket.Success || !pid.Success)
            {
                throw new InvalidOperationException($"Could not parse ssh-agent output: {output}");
            }

            var handle = new SshAgentHandle(socket.Groups[1].Value, pid.Groups[1].Value);
            await RunAsync(
                "ssh-add",
                $"\"{keyPath}\"",
                environment: new Dictionary<string, string> { ["SSH_AUTH_SOCK"] = handle.SocketPath });
            return handle;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await RunAsync(
                    "ssh-agent",
                    "-k",
                    environment: new Dictionary<string, string> { ["SSH_AGENT_PID"] = _pid });
            }
            catch
            {
                // Best effort; a leaked agent dies with the CI runner.
            }
        }

        private static async Task<string> RunAsync(string fileName, string arguments, IReadOnlyDictionary<string, string>? environment)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            if (environment != null)
            {
                foreach ((string key, string value) in environment)
                {
                    startInfo.Environment[key] = value;
                }
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start {fileName}.");
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{fileName} {arguments} exited {process.ExitCode}: {error}");
            }

            return output;
        }
    }
}
