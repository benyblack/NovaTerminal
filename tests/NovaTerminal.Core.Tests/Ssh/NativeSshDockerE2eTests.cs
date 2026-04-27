using NovaTerminal.Core.Replay;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Core.Ssh.Sessions;
using NovaTerminal.Core.Tests.Infra;
using System.Text;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeSshDockerE2eTests
{
    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_CanDownloadFile()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string remotePath = "/tmp/native-sftp-source.txt";
            const string expected = "native-sftp-download";
            await fixture.WriteTextFileAsync(remotePath, expected);

            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            string localPath = Path.Combine(tempRoot, "downloaded.txt");

            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Download,
                Kind = NativeSftpTransferKind.File,
                RemotePath = remotePath,
                LocalPath = localPath
            };

            interop.RunSftpTransfer(connection, transfer, progress: null, CancellationToken.None);

            Assert.Equal(expected, File.ReadAllText(localPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_CanReportProgressDuringDownload()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string remotePath = "/tmp/native-sftp-progress-source.txt";
            string expected = string.Concat(Enumerable.Repeat("native-sftp-progress-", 32 * 1024));
            await fixture.WriteTextFileAsync(remotePath, expected);

            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            string localPath = Path.Combine(tempRoot, "downloaded-progress.txt");
            var progressUpdates = new List<NativeSftpTransferProgress>();

            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Download,
                Kind = NativeSftpTransferKind.File,
                RemotePath = remotePath,
                LocalPath = localPath
            };

            interop.RunSftpTransfer(
                connection,
                transfer,
                progress: update => progressUpdates.Add(update),
                CancellationToken.None);

            long expectedBytes = Encoding.UTF8.GetByteCount(expected);

            Assert.True(progressUpdates.Count > 1, $"Expected multiple progress updates for a multi-chunk download, but observed {progressUpdates.Count}.");
            Assert.All(progressUpdates, update => Assert.Equal(remotePath, update.CurrentPath));
            Assert.Contains(progressUpdates, update => update.BytesTotal > 0);
            Assert.All(progressUpdates, update => Assert.True(update.BytesDone > 0, "Expected progress updates to report copied bytes."));
            Assert.Contains(progressUpdates, update => update.BytesDone < expectedBytes);
            Assert.Contains(progressUpdates, update => update.BytesTotal == expectedBytes);
            Assert.Contains(
                Enumerable.Range(1, progressUpdates.Count - 1),
                index => progressUpdates[index - 1].BytesDone < progressUpdates[index].BytesDone);
            for (int i = 1; i < progressUpdates.Count; i++)
            {
                Assert.True(
                    progressUpdates[i - 1].BytesDone <= progressUpdates[i].BytesDone,
                    $"Expected BytesDone to be monotonic, but observed {progressUpdates[i - 1].BytesDone} then {progressUpdates[i].BytesDone}.");
            }
            Assert.Equal(expectedBytes, progressUpdates[^1].BytesDone);
            Assert.Equal(expectedBytes, progressUpdates[^1].BytesTotal);
            Assert.Equal(expected, File.ReadAllText(localPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_CanUploadFile()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string expected = "native-sftp-upload";
            const string remotePath = "/tmp/native-sftp-upload.txt";
            string localPath = Path.Combine(tempRoot, "upload.txt");
            await File.WriteAllTextAsync(localPath, expected);

            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Upload,
                Kind = NativeSftpTransferKind.File,
                LocalPath = localPath,
                RemotePath = remotePath
            };

            interop.RunSftpTransfer(connection, transfer, progress: null, CancellationToken.None);

            Assert.Equal(expected, await fixture.ReadTextFileAsync(remotePath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_CanUploadFileToExistingRemoteDirectory()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-upload-dir-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string expected = "native-sftp-upload-dir-target";
            string localPath = Path.Combine(tempRoot, "upload-to-dir.txt");
            await File.WriteAllTextAsync(localPath, expected);

            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Upload,
                Kind = NativeSftpTransferKind.File,
                LocalPath = localPath,
                RemotePath = "/tmp"
            };

            interop.RunSftpTransfer(connection, transfer, progress: null, CancellationToken.None);

            Assert.Equal(expected, await fixture.ReadTextFileAsync("/tmp/upload-to-dir.txt"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_CanUploadFileToHomeDirectoryShortcut()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-upload-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string expected = "native-sftp-upload-home";
            string localPath = Path.Combine(tempRoot, "upload-home.txt");
            await File.WriteAllTextAsync(localPath, expected);

            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Upload,
                Kind = NativeSftpTransferKind.File,
                LocalPath = localPath,
                RemotePath = "~"
            };

            interop.RunSftpTransfer(connection, transfer, progress: null, CancellationToken.None);

            Assert.Equal(expected, await fixture.ReadTextFileAsync("/home/nova/upload-home.txt"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_CanDownloadDirectory()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-download-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string remoteRoot = "/tmp/native-sftp-download-dir";
            await fixture.CreateDirectoryAsync($"{remoteRoot}/nested");
            await fixture.WriteTextFileAsync($"{remoteRoot}/a.txt", "alpha");
            await fixture.WriteTextFileAsync($"{remoteRoot}/nested/b.txt", "beta");

            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Download,
                Kind = NativeSftpTransferKind.Directory,
                LocalPath = tempRoot,
                RemotePath = remoteRoot
            };

            interop.RunSftpTransfer(connection, transfer, progress: null, CancellationToken.None);

            string localRoot = Path.Combine(tempRoot, "native-sftp-download-dir");
            Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(localRoot, "a.txt")));
            Assert.Equal("beta", await File.ReadAllTextAsync(Path.Combine(localRoot, "nested", "b.txt")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_CanReportProgressDuringDirectoryDownload()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-download-dir-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string remoteRoot = "/tmp/native-sftp-download-dir-progress";
            string nestedRemotePath = $"{remoteRoot}/nested";
            string remoteFileA = $"{remoteRoot}/a.txt";
            string remoteFileB = $"{nestedRemotePath}/b.txt";
            string expectedA = string.Concat(Enumerable.Repeat("alpha-", 32 * 1024));
            string expectedB = string.Concat(Enumerable.Repeat("beta-", 32 * 1024));
            await fixture.CreateDirectoryAsync(nestedRemotePath);
            await fixture.WriteTextFileAsync(remoteFileA, expectedA);
            await fixture.WriteTextFileAsync(remoteFileB, expectedB);

            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            var progressUpdates = new List<NativeSftpTransferProgress>();
            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Download,
                Kind = NativeSftpTransferKind.Directory,
                LocalPath = tempRoot,
                RemotePath = remoteRoot
            };

            interop.RunSftpTransfer(
                connection,
                transfer,
                progress: update => progressUpdates.Add(update),
                CancellationToken.None);

            string localRoot = Path.Combine(tempRoot, "native-sftp-download-dir-progress");
            Assert.Equal(expectedA, await File.ReadAllTextAsync(Path.Combine(localRoot, "a.txt")));
            Assert.Equal(expectedB, await File.ReadAllTextAsync(Path.Combine(localRoot, "nested", "b.txt")));

            long expectedBytesA = Encoding.UTF8.GetByteCount(expectedA);
            long expectedBytesB = Encoding.UTF8.GetByteCount(expectedB);
            var updatesByPath = new Dictionary<string, List<NativeSftpTransferProgress>>(StringComparer.Ordinal);
            foreach (NativeSftpTransferProgress update in progressUpdates)
            {
                string path = Assert.IsType<string>(update.CurrentPath);
                if (!updatesByPath.TryGetValue(path, out List<NativeSftpTransferProgress>? updates))
                {
                    updates = new List<NativeSftpTransferProgress>();
                    updatesByPath[path] = updates;
                }

                updates.Add(update);
            }

            Assert.True(progressUpdates.Count > 2, $"Expected multiple progress callbacks during directory download, but observed {progressUpdates.Count}.");
            Assert.All(
                progressUpdates,
                update => Assert.False(string.IsNullOrWhiteSpace(update.CurrentPath), "Expected each progress callback to include a current path."));
            Assert.True(updatesByPath.ContainsKey(remoteFileA), $"Expected progress updates for {remoteFileA}.");
            Assert.True(updatesByPath.ContainsKey(remoteFileB), $"Expected progress updates for {remoteFileB}.");
            Assert.Contains(progressUpdates, update => update.BytesTotal > 0);
            Assert.All(progressUpdates, update => Assert.True(update.BytesDone > 0, "Expected directory progress updates to report copied bytes."));
            Assert.True(updatesByPath[remoteFileA].Count > 1, $"Expected multiple progress updates for {remoteFileA}, but observed {updatesByPath[remoteFileA].Count}.");
            Assert.True(updatesByPath[remoteFileB].Count > 1, $"Expected multiple progress updates for {remoteFileB}, but observed {updatesByPath[remoteFileB].Count}.");
            AssertPathProgress(remoteFileA, updatesByPath[remoteFileA], expectedBytesA);
            AssertPathProgress(remoteFileB, updatesByPath[remoteFileB], expectedBytesB);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void AssertPathProgress(string remotePath, IReadOnlyList<NativeSftpTransferProgress> updates, long expectedBytes)
    {
        Assert.All(updates, update => Assert.Equal(remotePath, update.CurrentPath));
        Assert.Contains(updates, update => update.BytesTotal > 0);
        Assert.Contains(updates, update => update.BytesDone < expectedBytes);
        Assert.Contains(updates, update => update.BytesTotal == expectedBytes);
        Assert.Contains(
            Enumerable.Range(1, updates.Count - 1),
            index => updates[index - 1].BytesDone < updates[index].BytesDone);
        for (int i = 1; i < updates.Count; i++)
        {
            Assert.True(
                updates[i - 1].BytesDone <= updates[i].BytesDone,
                $"Expected BytesDone for {remotePath} to be monotonic, but observed {updates[i - 1].BytesDone} then {updates[i].BytesDone}.");
        }

        Assert.Equal(expectedBytes, updates[^1].BytesDone);
        Assert.Equal(expectedBytes, updates[^1].BytesTotal);
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_CanUploadDirectory()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-upload-dir-{Guid.NewGuid():N}");
        string localRoot = Path.Combine(tempRoot, "native-sftp-upload-dir");
        Directory.CreateDirectory(Path.Combine(localRoot, "nested"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(localRoot, "a.txt"), "alpha");
            await File.WriteAllTextAsync(Path.Combine(localRoot, "nested", "b.txt"), "beta");

            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Upload,
                Kind = NativeSftpTransferKind.Directory,
                LocalPath = localRoot,
                RemotePath = "/tmp"
            };

            interop.RunSftpTransfer(connection, transfer, progress: null, CancellationToken.None);

            Assert.Equal("alpha", await fixture.ReadTextFileAsync("/tmp/native-sftp-upload-dir/a.txt"));
            Assert.Equal("beta", await fixture.ReadTextFileAsync("/tmp/native-sftp-upload-dir/nested/b.txt"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_MissingRemotePath_ReturnsClearError()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-missing-remote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Download,
                Kind = NativeSftpTransferKind.File,
                LocalPath = Path.Combine(tempRoot, "missing.txt"),
                RemotePath = "/tmp/does-not-exist.txt"
            };

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => interop.RunSftpTransfer(connection, transfer, progress: null, CancellationToken.None));

            Assert.Contains("Remote path not found: /tmp/does-not-exist.txt", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_MissingLocalPath_ReturnsClearError()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-missing-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = fixture.Password,
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Upload,
                Kind = NativeSftpTransferKind.File,
                LocalPath = Path.Combine(tempRoot, "does-not-exist.txt"),
                RemotePath = "/tmp/upload.txt"
            };

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => interop.RunSftpTransfer(connection, transfer, progress: null, CancellationToken.None));

            Assert.Contains("Local path not found:", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSftp_BadPassword_ReturnsAuthenticationFailed()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"nova-native-sftp-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            await fixture.WriteTextFileAsync("/tmp/auth-check.txt", "auth");
            SshHostKeyInfo hostKey = await fixture.GetHostKeyAsync();
            string knownHostsPath = Path.Combine(tempRoot, "native_known_hosts.json");
            var knownHosts = new NativeKnownHostsStore(knownHostsPath);
            knownHosts.TrustHost(fixture.Host, fixture.Port, hostKey.Algorithm, hostKey.Fingerprint);

            var interop = new NativeSshInterop();
            var connection = new NativeSshConnectionOptions
            {
                Host = fixture.Host,
                User = fixture.UserName,
                Port = fixture.Port,
                Password = "wrong-password",
                KnownHostsFilePath = knownHostsPath
            };
            var transfer = new NativeSftpTransferOptions
            {
                Direction = NativeSftpTransferDirection.Download,
                Kind = NativeSftpTransferKind.File,
                LocalPath = Path.Combine(tempRoot, "auth.txt"),
                RemotePath = "/tmp/auth-check.txt"
            };

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => interop.RunSftpTransfer(connection, transfer, progress: null, CancellationToken.None));

            Assert.Contains("Authentication failed", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSsh_CanAuthenticate_RunCommand_AndReturnToPrompt()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);
        var logs = new List<string>();

        using var session = new NativeSshSession(
            CreateProfile(fixture),
            cols: 120,
            rows: 30,
            log: logs.Add,
            interactionHandler: handler);

        session.AttachBuffer(buffer);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContains(buffer, "nova$"), TimeSpan.FromSeconds(20), "initial prompt");

        session.SendInput("printf 'hello\\n'\n");

        await WaitUntilAsync(
            () => SnapshotContains(buffer, "hello") && SnapshotContainsExactLine(buffer, "nova$"),
            TimeSpan.FromSeconds(20),
            "command output and prompt return");

        BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);

        Assert.Contains(snapshot.Lines, line => line.Contains("printf 'hello\\n'", StringComparison.Ordinal));
        Assert.Contains(snapshot.Lines, line => line == "hello");
        Assert.Contains(snapshot.Lines, line => line == "nova$");
        Assert.Contains(handler.Requests, request =>
            request.Kind == NovaTerminal.Core.Ssh.Interactions.SshInteractionKind.UnknownHostKey
            || request.Kind == NovaTerminal.Core.Ssh.Interactions.SshInteractionKind.ChangedHostKey);
        Assert.Contains(handler.Requests, request => request.Kind == NovaTerminal.Core.Ssh.Interactions.SshInteractionKind.Password);
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSsh_CanExitAlternateScreen_AndReturnToPrompt()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);
        var logs = new List<string>();
        const string altMarker = "ALT SCREEN LIVE";

        using var session = new NativeSshSession(
            CreateProfile(fixture),
            cols: 120,
            rows: 30,
            log: logs.Add,
            interactionHandler: handler);

        session.AttachBuffer(buffer);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContainsExactLine(buffer, "nova$"), TimeSpan.FromSeconds(20), "initial prompt");

        session.SendInput($"printf '{BuildAltScreenPayload(altMarker)}'\n");

        await WaitUntilAsync(
            () => SnapshotContainsExactLine(buffer, "nova$"),
            TimeSpan.FromSeconds(20),
            "prompt after alternate screen exit");

        BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);

        Assert.False(snapshot.IsAltScreen);
        Assert.DoesNotContain(snapshot.Lines, line => line.Contains(altMarker, StringComparison.Ordinal));
        Assert.Contains(snapshot.Lines, line => line == "nova$");
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSsh_CanResizeDuringAlternateScreen_AndKeepPromptUsable()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);
        var logs = new List<string>();
        const string altMarker = "ALT RESIZE LIVE";
        const int finalCols = 120;
        const int finalRows = 35;

        using var session = new NativeSshSession(
            CreateProfile(fixture),
            cols: 120,
            rows: 30,
            log: logs.Add,
            interactionHandler: handler);

        session.AttachBuffer(buffer);
        session.OnOutputReceived += parser.Process;

        await WaitUntilAsync(() => SnapshotContainsExactLine(buffer, "nova$"), TimeSpan.FromSeconds(20), "initial prompt");

        session.SendInput($"printf '\\033[?1049h{ToPrintfOctalLiteral(altMarker)}'; sleep 1; printf '\\033[?1049l'\n");

        await Task.Delay(250);
        ResizeTerminal(buffer, session, 100, 25);
        await Task.Delay(100);
        ResizeTerminal(buffer, session, 90, 20);
        await Task.Delay(100);
        ResizeTerminal(buffer, session, finalCols, finalRows);

        await WaitUntilAsync(
            () => SnapshotContainsExactLine(buffer, "nova$"),
            TimeSpan.FromSeconds(20),
            "prompt after resize burst");

        session.SendInput("stty size\n");

        await WaitUntilAsync(
            () => SnapshotContainsExactLine(buffer, $"{finalRows} {finalCols}") && SnapshotContainsExactLine(buffer, "nova$"),
            TimeSpan.FromSeconds(20),
            "final terminal size and prompt");

        BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);

        Assert.False(snapshot.IsAltScreen);
        Assert.DoesNotContain(snapshot.Lines, line => line.Contains(altMarker, StringComparison.Ordinal));
        Assert.Contains(snapshot.Lines, line => line == $"{finalRows} {finalCols}");
        Assert.Contains(snapshot.Lines, line => line == "nova$");
    }

    [DockerFact]
    [Trait("Category", "DockerE2E")]
    [Trait("Target", "NativeSsh")]
    public async Task NativeSsh_VimDownwardScroll_UpdatesVisibleWindowWithoutCtrlL()
    {
        await using var fixture = await DockerSshFixture.StartAsync();

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var handler = new NativeSshTestInteractionHandler(fixture.Password);
        var logs = new List<string>();
        var scrollOutput = new List<string>();
        bool captureScrollOutput = false;

        using var session = new NativeSshSession(
            CreateProfile(fixture),
            cols: 80,
            rows: 24,
            log: logs.Add,
            interactionHandler: handler);

        session.AttachBuffer(buffer);
        session.OnOutputReceived += text =>
        {
            if (captureScrollOutput)
            {
                scrollOutput.Add(text);
            }

            parser.Process(text);
        };

        await WaitUntilAsync(() => SnapshotContainsExactLine(buffer, "nova$"), TimeSpan.FromSeconds(20), "initial prompt");

        session.SendInput("for i in $(seq -w 1 40); do echo \"line $i\"; done >/tmp/vim-scroll.txt\n");
        await WaitUntilAsync(() => SnapshotContainsExactLine(buffer, "nova$"), TimeSpan.FromSeconds(20), "prompt after creating vim test file");

        session.SendInput("vim.tiny -u NONE -N -n +'set scrolloff=5' /tmp/vim-scroll.txt\n");

        await WaitUntilAsync(
            () =>
            {
                BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);
                return snapshot.IsAltScreen && snapshot.Lines.Any(line => line.Contains("line 01", StringComparison.Ordinal));
            },
            TimeSpan.FromSeconds(20),
            "vim initial screen");

        BufferSnapshot initial = BufferSnapshot.Capture(buffer);

        captureScrollOutput = true;
        session.SendInput(new string('j', 35));

        await WaitUntilAsync(
            () =>
            {
                BufferSnapshot snapshot = BufferSnapshot.Capture(buffer);
                return snapshot.IsAltScreen && snapshot.Lines.Any(line => line.Contains("line 40", StringComparison.Ordinal));
            },
            TimeSpan.FromSeconds(20),
            "vim downward scroll to later file content");

        BufferSnapshot after = BufferSnapshot.Capture(buffer);
        captureScrollOutput = false;
        int changedEditingRows = CountChangedRows(initial, after, editingRows: 20);

        Assert.True(
            changedEditingRows >= 5,
            $"Expected vim downward scroll to update multiple editing rows, but only {changedEditingRows} rows changed.{Environment.NewLine}Before:{Environment.NewLine}{FormatLines(initial)}{Environment.NewLine}After:{Environment.NewLine}{FormatLines(after)}{Environment.NewLine}Raw:{Environment.NewLine}{EscapeControlText(string.Concat(scrollOutput))}");
        Assert.Contains(after.Lines, line => line.Contains("line 40", StringComparison.Ordinal));
        Assert.DoesNotContain(after.Lines.Take(20), line => line.Contains("line 01", StringComparison.Ordinal));
    }

    private static SshProfile CreateProfile(DockerSshFixture fixture)
    {
        return new SshProfile
        {
            Id = Guid.Parse("d0ec8131-f7fe-40b3-992d-174da56fa5cd"),
            Name = "Docker Native SSH",
            BackendKind = SshBackendKind.Native,
            Host = fixture.Host,
            User = fixture.UserName,
            Port = fixture.Port
        };
    }

    private static bool SnapshotContains(TerminalBuffer buffer, string value)
    {
        return BufferSnapshot.Capture(buffer).Lines.Any(line => line.Contains(value, StringComparison.Ordinal));
    }

    private static bool SnapshotContainsExactLine(TerminalBuffer buffer, string value)
    {
        return BufferSnapshot.Capture(buffer).Lines.Any(line => line == value);
    }

    private static void ResizeTerminal(TerminalBuffer buffer, NativeSshSession session, int cols, int rows)
    {
        buffer.Resize(cols, rows);
        session.Resize(cols, rows);
    }

    private static int CountChangedRows(BufferSnapshot before, BufferSnapshot after, int editingRows)
    {
        IReadOnlyList<string> beforeLines = before.Lines.ToArray();
        IReadOnlyList<string> afterLines = after.Lines.ToArray();
        int rowCount = Math.Min(Math.Min(beforeLines.Count, afterLines.Count), editingRows);
        int changed = 0;
        for (int i = 0; i < rowCount; i++)
        {
            if (!string.Equals(beforeLines[i], afterLines[i], StringComparison.Ordinal))
            {
                changed++;
            }
        }

        return changed;
    }

    private static bool SnapshotsEqual(BufferSnapshot before, BufferSnapshot after)
    {
        IReadOnlyList<string> beforeLines = before.Lines.ToArray();
        IReadOnlyList<string> afterLines = after.Lines.ToArray();
        if (before.IsAltScreen != after.IsAltScreen || beforeLines.Count != afterLines.Count)
        {
            return false;
        }

        for (int i = 0; i < beforeLines.Count; i++)
        {
            if (!string.Equals(beforeLines[i], afterLines[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatLines(BufferSnapshot snapshot)
    {
        return string.Join(
            Environment.NewLine,
            snapshot.Lines.Select((line, index) => $"{index:D2}: {line}"));
    }

    private static string EscapeControlText(string text)
    {
        var builder = new StringBuilder();
        foreach (char c in text)
        {
            switch (c)
            {
                case '\u001b':
                    builder.Append("\\e");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        builder.Append("\\x");
                        builder.Append(((int)c).ToString("X2"));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }

        return builder.ToString();
    }

    private static string BuildAltScreenPayload(string text)
    {
        return $"\\033[?1049h{ToPrintfOctalLiteral(text)}\\033[?1049l";
    }

    private static string ToPrintfOctalLiteral(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var builder = new StringBuilder(bytes.Length * 4);
        foreach (byte value in bytes)
        {
            builder.Append('\\');
            builder.Append(Convert.ToString(value, 8).PadLeft(3, '0'));
        }

        return builder.ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string description)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.True(predicate(), $"Timed out waiting for {description}.");
    }
}
