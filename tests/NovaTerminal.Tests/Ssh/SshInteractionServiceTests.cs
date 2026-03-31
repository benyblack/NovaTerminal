using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NovaTerminal.Core.Ssh.Interactions;
using NovaTerminal.Core.Ssh.Native;
using NovaTerminal.Services.Ssh;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class SshInteractionServiceTests
{
    [AvaloniaFact]
    public async Task BackgroundThreadRequests_AreMarshalledToUiThread()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            var owner = new Window();
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            int presenterThreadId = -1;
            int ownerThreadId = -1;

            var service = new SshInteractionService(
                ownerProvider: () =>
                {
                    Assert.True(Dispatcher.UIThread.CheckAccess());
                    ownerThreadId = Environment.CurrentManagedThreadId;
                    return owner;
                },
                hostKeyPresenter: (_, _, _) =>
                {
                    Assert.True(Dispatcher.UIThread.CheckAccess());
                    presenterThreadId = Environment.CurrentManagedThreadId;
                    return Task.FromResult(SshInteractionResponse.AcceptHostKey());
                },
                knownHostsStore: knownHosts);

            int workerThreadId = -1;
            var response = await Task.Run(async () =>
            {
                workerThreadId = Environment.CurrentManagedThreadId;
                return await service.HandleAsync(new SshInteractionRequest
                {
                    Kind = SshInteractionKind.UnknownHostKey,
                    Host = "example.internal",
                    Port = 22,
                    Algorithm = "ssh-ed25519",
                    Fingerprint = "SHA256:test"
                }, CancellationToken.None);
            });

            Assert.True(response.IsAccepted);
            Assert.NotEqual(-1, ownerThreadId);
            Assert.NotEqual(-1, presenterThreadId);
            Assert.NotEqual(workerThreadId, ownerThreadId);
            Assert.NotEqual(workerThreadId, presenterThreadId);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HostKeyRequestsMapToHostKeyPromptViewModel()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            HostKeyPromptViewModel? capturedVm = null;
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            var service = new SshInteractionService(
                hostKeyPresenter: (_, vm, _) =>
                {
                    capturedVm = vm;
                    return Task.FromResult(SshInteractionResponse.AcceptHostKey());
                },
                knownHostsStore: knownHosts);

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.UnknownHostKey,
                Host = "example.internal",
                Port = 2222,
                Algorithm = "ssh-ed25519",
                Fingerprint = "SHA256:test"
            }, CancellationToken.None);

            Assert.NotNull(capturedVm);
            Assert.Equal("example.internal", capturedVm!.Host);
            Assert.Equal(2222, capturedVm.Port);
            Assert.Equal("ssh-ed25519", capturedVm.Algorithm);
            Assert.Equal("SHA256:test", capturedVm.Fingerprint);
            Assert.False(capturedVm.IsChangedHostKey);
            Assert.True(response.IsAccepted);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TrustedHostKey_SkipsDialogAndAcceptsImmediately()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            knownHosts.TrustHost("example.internal", 22, "ssh-ed25519", "SHA256:test");
            int promptCount = 0;
            var service = new SshInteractionService(
                hostKeyPresenter: (_, _, _) =>
                {
                    promptCount++;
                    return Task.FromResult(SshInteractionResponse.Cancel());
                },
                knownHostsStore: knownHosts);

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.UnknownHostKey,
                Host = "example.internal",
                Port = 22,
                Algorithm = "ssh-ed25519",
                Fingerprint = "SHA256:test"
            }, CancellationToken.None);

            Assert.True(response.IsAccepted);
            Assert.Equal(0, promptCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedHostKey_MapsToChangedPromptViewModel()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            knownHosts.TrustHost("example.internal", 22, "ssh-ed25519", "SHA256:old");
            HostKeyPromptViewModel? capturedVm = null;
            var service = new SshInteractionService(
                hostKeyPresenter: (_, vm, _) =>
                {
                    capturedVm = vm;
                    return Task.FromResult(SshInteractionResponse.AcceptHostKey());
                },
                knownHostsStore: knownHosts);

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.UnknownHostKey,
                Host = "example.internal",
                Port = 22,
                Algorithm = "ssh-ed25519",
                Fingerprint = "SHA256:new"
            }, CancellationToken.None);

            Assert.NotNull(capturedVm);
            Assert.True(capturedVm!.IsChangedHostKey);
            Assert.True(response.IsAccepted);
            Assert.Equal(NativeKnownHostMatch.Trusted, knownHosts.CheckHost("example.internal", 22, "ssh-ed25519", "SHA256:new"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PasswordRequestsMapToSingleSecretPrompt()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.FromSecret("password"));
            });

        var response = await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.Password,
            Prompt = "Password:"
        }, CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.Equal("Password", capturedVm!.Title);
        Assert.Single(capturedVm.Prompts);
        Assert.True(capturedVm.Prompts[0].IsSecret);
        Assert.Equal("Password:", capturedVm.Prompts[0].Prompt);
        Assert.Equal("password", response.Secret);
    }

    [Fact]
    public async Task PassphraseRequestsMapToSecretPrompt()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.FromSecret("hunter2"));
            });

        var response = await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.Passphrase,
            Prompt = "Key passphrase:"
        }, CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.Equal("Passphrase", capturedVm!.Title);
        Assert.Single(capturedVm.Prompts);
        Assert.True(capturedVm.Prompts[0].IsSecret);
        Assert.Equal("hunter2", response.Secret);
    }

    [Fact]
    public async Task KeyboardInteractiveRequestsMapAllPromptFields()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.FromKeyboardResponses("code", "otp"));
            });

        var response = await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.KeyboardInteractive,
            Name = "Duo",
            Instructions = "Provide challenge response",
            KeyboardPrompts =
            [
                new SshKeyboardPrompt("Passcode:", false),
                new SshKeyboardPrompt("OTP:", false)
            ]
        }, CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.Equal("Duo", capturedVm!.Title);
        Assert.Equal("Provide challenge response", capturedVm.Message);
        Assert.Equal(2, capturedVm.Prompts.Count);
        Assert.Equal("Passcode:", capturedVm.Prompts[0].Prompt);
        Assert.True(capturedVm.Prompts[0].IsSecret);
        Assert.Equal(["code", "otp"], response.KeyboardResponses);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_interaction_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
