namespace NovaTerminal.Core.Ssh.Interactions;

public sealed class SshInteractionResponse
{
    public bool IsAccepted { get; init; }
    public bool IsCanceled { get; init; }
    public string Secret { get; init; } = string.Empty;
    public IReadOnlyList<string> KeyboardResponses { get; init; } = Array.Empty<string>();

    public static SshInteractionResponse AcceptHostKey() => new() { IsAccepted = true };

    public static SshInteractionResponse Cancel() => new() { IsCanceled = true };

    public static SshInteractionResponse FromSecret(string secret) => new() { Secret = secret ?? string.Empty };

    public static SshInteractionResponse FromKeyboardResponses(params string[] responses) =>
        new() { KeyboardResponses = responses ?? Array.Empty<string>() };
}
