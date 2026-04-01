using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NovaTerminal.Core.Ssh.Interactions;

namespace NovaTerminal.ViewModels.Ssh;

public sealed class AuthPromptViewModel
{
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool CanRememberPassword { get; init; }
    public bool RememberPassword { get; set; }
    public ObservableCollection<AuthPromptEntryViewModel> Prompts { get; } = new();

    public SshInteractionResponse BuildResponse()
    {
        if (Prompts.Count == 1)
        {
            return SshInteractionResponse.FromSecret(Prompts[0].Value, RememberPassword);
        }

        return SshInteractionResponse.FromKeyboardResponses(Prompts.Select(prompt => prompt.Value).ToArray());
    }
}

public sealed class AuthPromptEntryViewModel : INotifyPropertyChanged
{
    private string _value = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Prompt { get; init; } = string.Empty;
    public bool IsSecret { get; init; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }

    public string PasswordCharText => IsSecret ? "*" : string.Empty;
}
