using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using NovaTerminal.Core.Ssh.Interactions;
using NovaTerminal.ViewModels.Ssh;
using NovaTerminal.Views.Ssh;

namespace NovaTerminal.Services.Ssh;

public sealed class SshInteractionService : ISshInteractionService
{
    private readonly Func<Window?> _ownerProvider;
    private readonly Action<Window>? _prepareDialog;
    private readonly Func<Window?, HostKeyPromptViewModel, CancellationToken, Task<SshInteractionResponse>> _hostKeyPresenter;
    private readonly Func<Window?, AuthPromptViewModel, CancellationToken, Task<SshInteractionResponse>> _authPresenter;

    public SshInteractionService(
        Func<Window?>? ownerProvider = null,
        Action<Window>? prepareDialog = null,
        Func<Window?, HostKeyPromptViewModel, CancellationToken, Task<SshInteractionResponse>>? hostKeyPresenter = null,
        Func<Window?, AuthPromptViewModel, CancellationToken, Task<SshInteractionResponse>>? authPresenter = null)
    {
        _ownerProvider = ownerProvider ?? (() => null);
        _prepareDialog = prepareDialog;
        _hostKeyPresenter = hostKeyPresenter ?? PresentHostKeyAsync;
        _authPresenter = authPresenter ?? PresentAuthAsync;
    }

    public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Window? owner = _ownerProvider();
        return request.Kind switch
        {
            SshInteractionKind.UnknownHostKey or SshInteractionKind.ChangedHostKey => _hostKeyPresenter(owner, CreateHostKeyViewModel(request), cancellationToken),
            SshInteractionKind.Password => _authPresenter(owner, CreatePasswordViewModel(request), cancellationToken),
            SshInteractionKind.Passphrase => _authPresenter(owner, CreatePassphraseViewModel(request), cancellationToken),
            SshInteractionKind.KeyboardInteractive => _authPresenter(owner, CreateKeyboardViewModel(request), cancellationToken),
            _ => Task.FromResult(SshInteractionResponse.Cancel())
        };
    }

    private async Task<SshInteractionResponse> PresentHostKeyAsync(Window? owner, HostKeyPromptViewModel viewModel, CancellationToken cancellationToken)
    {
        if (owner == null || cancellationToken.IsCancellationRequested)
        {
            return SshInteractionResponse.Cancel();
        }

        var dialog = new HostKeyPromptDialog(viewModel);
        _prepareDialog?.Invoke(dialog);
        return await dialog.ShowDialog<SshInteractionResponse?>(owner) ?? SshInteractionResponse.Cancel();
    }

    private async Task<SshInteractionResponse> PresentAuthAsync(Window? owner, AuthPromptViewModel viewModel, CancellationToken cancellationToken)
    {
        if (owner == null || cancellationToken.IsCancellationRequested)
        {
            return SshInteractionResponse.Cancel();
        }

        var dialog = new AuthPromptDialog(viewModel);
        _prepareDialog?.Invoke(dialog);
        return await dialog.ShowDialog<SshInteractionResponse?>(owner) ?? SshInteractionResponse.Cancel();
    }

    private static HostKeyPromptViewModel CreateHostKeyViewModel(SshInteractionRequest request)
    {
        return new HostKeyPromptViewModel
        {
            Host = request.Host,
            Port = request.Port,
            Algorithm = request.Algorithm,
            Fingerprint = request.Fingerprint,
            IsChangedHostKey = request.Kind == SshInteractionKind.ChangedHostKey
        };
    }

    private static AuthPromptViewModel CreatePasswordViewModel(SshInteractionRequest request)
    {
        var viewModel = new AuthPromptViewModel
        {
            Title = "Password",
            Message = "Enter the SSH password to continue."
        };
        viewModel.Prompts.Add(new AuthPromptEntryViewModel
        {
            Prompt = string.IsNullOrWhiteSpace(request.Prompt) ? "Password:" : request.Prompt,
            IsSecret = true
        });
        return viewModel;
    }

    private static AuthPromptViewModel CreatePassphraseViewModel(SshInteractionRequest request)
    {
        var viewModel = new AuthPromptViewModel
        {
            Title = "Passphrase",
            Message = "Enter the private key passphrase to continue."
        };
        viewModel.Prompts.Add(new AuthPromptEntryViewModel
        {
            Prompt = string.IsNullOrWhiteSpace(request.Prompt) ? "Passphrase:" : request.Prompt,
            IsSecret = true
        });
        return viewModel;
    }

    private static AuthPromptViewModel CreateKeyboardViewModel(SshInteractionRequest request)
    {
        var viewModel = new AuthPromptViewModel
        {
            Title = string.IsNullOrWhiteSpace(request.Name) ? "Keyboard Interactive" : request.Name,
            Message = request.Instructions
        };

        foreach (SshKeyboardPrompt prompt in request.KeyboardPrompts)
        {
            viewModel.Prompts.Add(new AuthPromptEntryViewModel
            {
                Prompt = prompt.Prompt,
                IsSecret = !prompt.Echo
            });
        }

        return viewModel;
    }
}
