using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NovaTerminal.Core;
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
    public async Task NativePasswordRequests_WithProfileIdentity_ExposeRememberPasswordOption()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.Cancel());
            });

        await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.Password,
            Prompt = "Password:",
            ProfileId = Guid.Parse("531365f9-b488-4475-ab7d-7221e5056cae"),
            ProfileName = "Native Prod",
            ProfileUser = "alice",
            ProfileHost = "example.internal"
        }, CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.True(capturedVm!.CanRememberPassword);
    }

    [Fact]
    public async Task PasswordRequests_WithoutProfileIdentity_DoNotExposeRememberPasswordOption()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.Cancel());
            });

        await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.Password,
            Prompt = "Password:"
        }, CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.False(capturedVm!.CanRememberPassword);
    }

    [Fact]
    public async Task NativePasswordRequests_StoreSubmittedPassword_WhenRememberPasswordIsChecked()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string vaultPath = Path.Combine(tempRoot, "vault.dat");
            var vault = new VaultService(vaultPath);
            var profile = new TerminalProfile
            {
                Id = Guid.Parse("0c25f4e2-2c2d-4f16-9d70-4d01e7c8fdb1"),
                Type = ConnectionType.SSH,
                Name = "Native Prod",
                SshHost = "example.internal",
                SshUser = "alice"
            };

            var service = new SshInteractionService(
                vaultService: vault,
                authPresenter: (_, _, _) =>
                {
                    return Task.FromResult(SshInteractionResponse.FromSecret("manual-secret", rememberPasswordInVault: true));
                });

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.Password,
                Prompt = "Password:",
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ProfileUser = profile.SshUser,
                ProfileHost = profile.SshHost,
                AllowVaultPasswordReuse = false
            }, CancellationToken.None);

            Assert.Equal("manual-secret", response.Secret);
            Assert.Equal("manual-secret", vault.GetSshPasswordForProfile(profile));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task NativePasswordRequests_DoNotStoreSubmittedPassword_WhenRememberPasswordIsUnchecked()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string vaultPath = Path.Combine(tempRoot, "vault.dat");
            var vault = new VaultService(vaultPath);
            var profile = new TerminalProfile
            {
                Id = Guid.Parse("f0dbb9df-8e7e-4e11-9e0a-91de4a2f84be"),
                Type = ConnectionType.SSH,
                Name = "Native Prod",
                SshHost = "example.internal",
                SshUser = "alice"
            };

            var service = new SshInteractionService(
                vaultService: vault,
                authPresenter: (_, _, _) =>
                    Task.FromResult(SshInteractionResponse.FromSecret("manual-secret")));

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.Password,
                Prompt = "Password:",
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ProfileUser = profile.SshUser,
                ProfileHost = profile.SshHost,
                AllowVaultPasswordReuse = false
            }, CancellationToken.None);

            Assert.Equal("manual-secret", response.Secret);
            Assert.Null(vault.GetSshPasswordForProfile(profile));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task NativePasswordRequests_LeavingRememberUnchecked_PreservesExistingVaultSecret()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string vaultPath = Path.Combine(tempRoot, "vault.dat");
            var vault = new VaultService(vaultPath);
            var profile = new TerminalProfile
            {
                Id = Guid.Parse("6752a7e9-6fcb-4108-813a-45411cba6a6c"),
                Type = ConnectionType.SSH,
                Name = "Native Prod",
                SshHost = "example.internal",
                SshUser = "alice"
            };
            vault.SetSshPasswordForProfile(profile, "vault-secret");

            var service = new SshInteractionService(
                vaultService: vault,
                authPresenter: (_, _, _) =>
                    Task.FromResult(SshInteractionResponse.FromSecret("manual-secret")));

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.Password,
                Prompt = "Password:",
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ProfileUser = profile.SshUser,
                ProfileHost = profile.SshHost,
                AllowVaultPasswordReuse = false
            }, CancellationToken.None);

            Assert.Equal("manual-secret", response.Secret);
            Assert.Equal("vault-secret", vault.GetSshPasswordForProfile(profile));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PasswordRequests_AreAnsweredFromVaultWithoutShowingDialog_WhenProfileIdentityIsProvided()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string vaultPath = Path.Combine(tempRoot, "vault.dat");
            var vault = new VaultService(vaultPath);
            var profile = new TerminalProfile
            {
                Id = Guid.Parse("3b0a5c25-9b4d-4d6b-80b4-5cf8c2ef8e6a"),
                Type = ConnectionType.SSH,
                Name = "Native Prod",
                SshHost = "example.internal",
                SshUser = "alice"
            };
            vault.SetSshPasswordForProfile(profile, "vault-secret");

            int promptCount = 0;
            var service = new SshInteractionService(
                vaultService: vault,
                authPresenter: (_, _, _) =>
                {
                    promptCount++;
                    return Task.FromResult(SshInteractionResponse.Cancel());
                });

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.Password,
                Prompt = "Password:",
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ProfileUser = profile.SshUser,
                ProfileHost = profile.SshHost,
                AllowVaultPasswordReuse = true
            }, CancellationToken.None);

            Assert.Equal(0, promptCount);
            Assert.Equal("vault-secret", response.Secret);
            Assert.False(response.IsCanceled);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PasswordRequests_UseLegacyVaultSecret_WhenFullProfileIdentityIsProvided()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string vaultPath = Path.Combine(tempRoot, "vault.dat");
            var vault = new VaultService(vaultPath);
            var profile = new TerminalProfile
            {
                Id = Guid.Parse("19c4e5f0-62ed-4d56-90b1-1d53e4c90d19"),
                Type = ConnectionType.SSH,
                Name = "Legacy Prod",
                SshHost = "legacy.internal",
                SshUser = "alice"
            };
            string legacyKey = $"SSH:{profile.Name}:{profile.SshUser}@{profile.SshHost}";
            vault.SetSecret(legacyKey, "legacy-secret");

            int promptCount = 0;
            var service = new SshInteractionService(
                vaultService: vault,
                authPresenter: (_, _, _) =>
                {
                    promptCount++;
                    return Task.FromResult(SshInteractionResponse.Cancel());
                });

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.Password,
                Prompt = "Password:",
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ProfileUser = profile.SshUser,
                ProfileHost = profile.SshHost,
                AllowVaultPasswordReuse = true
            }, CancellationToken.None);

            Assert.Equal(0, promptCount);
            Assert.Equal("legacy-secret", response.Secret);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PasswordRequests_FallBackToDialog_WhenVaultReuseIsNotAllowed()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string vaultPath = Path.Combine(tempRoot, "vault.dat");
            var vault = new VaultService(vaultPath);
            var profile = new TerminalProfile
            {
                Id = Guid.Parse("52e5e0a7-51c6-4c9e-8d1a-7f74e9e6d7b8"),
                Type = ConnectionType.SSH,
                Name = "Native Prod",
                SshHost = "example.internal",
                SshUser = "alice"
            };
            vault.SetSshPasswordForProfile(profile, "vault-secret");

            int promptCount = 0;
            var service = new SshInteractionService(
                vaultService: vault,
                authPresenter: (_, _, _) =>
                {
                    promptCount++;
                    return Task.FromResult(SshInteractionResponse.FromSecret("manual-secret"));
                });

            SshInteractionResponse response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.Password,
                Prompt = "Password:",
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ProfileUser = profile.SshUser,
                ProfileHost = profile.SshHost,
                AllowVaultPasswordReuse = false
            }, CancellationToken.None);

            Assert.Equal("manual-secret", response.Secret);
            Assert.Equal(1, promptCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PassphraseRequests_ShowDialogEvenWhenVaultHasPassword()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string vaultPath = Path.Combine(tempRoot, "vault.dat");
            var vault = new VaultService(vaultPath);
            var profile = new TerminalProfile
            {
                Id = Guid.Parse("6e1df53b-5b8f-4d5c-bfd7-9ad6f4d3d5d5"),
                Type = ConnectionType.SSH,
                Name = "Native Prod",
                SshHost = "example.internal",
                SshUser = "alice"
            };
            vault.SetSshPasswordForProfile(profile, "vault-secret");

            int promptCount = 0;
            var service = new SshInteractionService(
                vaultService: vault,
                authPresenter: (_, vm, _) =>
                {
                    promptCount++;
                    Assert.Equal("Passphrase", vm.Title);
                    return Task.FromResult(SshInteractionResponse.FromSecret("entered-passphrase"));
                });

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.Passphrase,
                Prompt = "Key passphrase:",
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ProfileUser = profile.SshUser,
                ProfileHost = profile.SshHost
            }, CancellationToken.None);

            Assert.Equal(1, promptCount);
            Assert.Equal("entered-passphrase", response.Secret);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
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
