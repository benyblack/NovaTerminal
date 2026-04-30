namespace NovaTerminal.Core.Ssh.Interactions;

public sealed class SshInteractionRequest
{
    public SshInteractionKind Kind { get; init; }
    public Guid? SessionId { get; init; }
    public Guid? ProfileId { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public string ProfileUser { get; init; } = string.Empty;
    public string ProfileHost { get; init; } = string.Empty;
    public bool AllowVaultPasswordReuse { get; init; }
    public bool RememberPasswordInVault { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Algorithm { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Instructions { get; init; } = string.Empty;
    public IReadOnlyList<SshKeyboardPrompt> KeyboardPrompts { get; init; } = Array.Empty<SshKeyboardPrompt>();
}

public sealed class SshKeyboardPrompt
{
    public SshKeyboardPrompt(string prompt, bool echo)
    {
        Prompt = prompt ?? string.Empty;
        Echo = echo;
    }

    public string Prompt { get; }
    public bool Echo { get; }
}
