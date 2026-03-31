using NovaTerminal.Core.Ssh.Interactions;
using NovaTerminal.Services.Ssh;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class SshInteractionServiceTests
{
    [Fact]
    public async Task HostKeyRequestsMapToHostKeyPromptViewModel()
    {
        HostKeyPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            hostKeyPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.AcceptHostKey());
            });

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
}
