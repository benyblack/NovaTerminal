using System.Text.Json;
using System.Text.Json.Serialization;
using NovaTerminal.Core.Ssh.Interactions;

namespace NovaTerminal.Core.Ssh.Native;

public static class NativeSshInteractionJson
{
    public static SshInteractionRequest ParseRequest(NativeSshEventKind eventKind, ReadOnlySpan<byte> payload)
    {
        return eventKind switch
        {
            NativeSshEventKind.HostKeyPrompt => CreateHostKeyRequest(payload),
            NativeSshEventKind.PasswordPrompt => CreateTextPromptRequest(SshInteractionKind.Password, payload),
            NativeSshEventKind.PassphrasePrompt => CreateTextPromptRequest(SshInteractionKind.Passphrase, payload),
            NativeSshEventKind.KeyboardInteractivePrompt => CreateKeyboardRequest(payload),
            _ => throw new InvalidOperationException($"Unsupported interaction event '{eventKind}'.")
        };
    }

    public static byte[] BuildResponsePayload(NativeSshResponseKind responseKind, SshInteractionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return responseKind switch
        {
            NativeSshResponseKind.HostKeyDecision => JsonSerializer.SerializeToUtf8Bytes(
                new NativeHostKeyDecisionPayload { Accept = response.IsAccepted && !response.IsCanceled },
                NativeSshInteractionJsonContext.Default.NativeHostKeyDecisionPayload),
            NativeSshResponseKind.Password or NativeSshResponseKind.Passphrase => JsonSerializer.SerializeToUtf8Bytes(
                new NativeTextResponsePayload { Text = response.IsCanceled ? string.Empty : response.Secret ?? string.Empty },
                NativeSshInteractionJsonContext.Default.NativeTextResponsePayload),
            NativeSshResponseKind.KeyboardInteractive => JsonSerializer.SerializeToUtf8Bytes(
                new NativeKeyboardInteractiveResponsePayload
                {
                    Responses = response.IsCanceled ? Array.Empty<string>() : response.KeyboardResponses.ToArray()
                },
                NativeSshInteractionJsonContext.Default.NativeKeyboardInteractiveResponsePayload),
            _ => throw new InvalidOperationException($"Unsupported native SSH response kind '{responseKind}'.")
        };
    }

    private static SshInteractionRequest CreateHostKeyRequest(ReadOnlySpan<byte> payload)
    {
        NativeHostKeyPromptPayload? model = JsonSerializer.Deserialize(
            payload,
            NativeSshInteractionJsonContext.Default.NativeHostKeyPromptPayload);
        if (model == null)
        {
            throw new InvalidOperationException("Failed to parse host key prompt payload.");
        }

        return new SshInteractionRequest
        {
            Kind = SshInteractionKind.UnknownHostKey,
            Host = model.Host,
            Port = model.Port,
            Algorithm = model.Algorithm,
            Fingerprint = model.Fingerprint
        };
    }

    private static SshInteractionRequest CreateTextPromptRequest(SshInteractionKind kind, ReadOnlySpan<byte> payload)
    {
        NativeTextPromptPayload? model = JsonSerializer.Deserialize(
            payload,
            NativeSshInteractionJsonContext.Default.NativeTextPromptPayload);
        if (model == null)
        {
            throw new InvalidOperationException("Failed to parse authentication prompt payload.");
        }

        return new SshInteractionRequest
        {
            Kind = kind,
            Prompt = model.Prompt
        };
    }

    private static SshInteractionRequest CreateKeyboardRequest(ReadOnlySpan<byte> payload)
    {
        NativeKeyboardInteractivePromptPayload? model = JsonSerializer.Deserialize(
            payload,
            NativeSshInteractionJsonContext.Default.NativeKeyboardInteractivePromptPayload);
        if (model == null)
        {
            throw new InvalidOperationException("Failed to parse keyboard-interactive payload.");
        }

        return new SshInteractionRequest
        {
            Kind = SshInteractionKind.KeyboardInteractive,
            Name = model.Name,
            Instructions = model.Instructions,
            KeyboardPrompts = model.Prompts.Select(prompt => new SshKeyboardPrompt(prompt.Prompt, prompt.Echo)).ToArray()
        };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NativeHostKeyPromptPayload))]
[JsonSerializable(typeof(NativeTextPromptPayload))]
[JsonSerializable(typeof(NativeKeyboardInteractivePromptPayload))]
[JsonSerializable(typeof(NativeHostKeyDecisionPayload))]
[JsonSerializable(typeof(NativeTextResponsePayload))]
[JsonSerializable(typeof(NativeKeyboardInteractiveResponsePayload))]
internal partial class NativeSshInteractionJsonContext : JsonSerializerContext
{
}

internal sealed class NativeHostKeyPromptPayload
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Algorithm { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
}

internal sealed class NativeTextPromptPayload
{
    public string Prompt { get; set; } = string.Empty;
}

internal sealed class NativeKeyboardInteractivePromptPayload
{
    public string Name { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public List<NativeKeyboardInteractivePromptItem> Prompts { get; set; } = new();
}

internal sealed class NativeKeyboardInteractivePromptItem
{
    public string Prompt { get; set; } = string.Empty;
    public bool Echo { get; set; }
}

internal sealed class NativeHostKeyDecisionPayload
{
    public bool Accept { get; set; }
}

internal sealed class NativeTextResponsePayload
{
    public string Text { get; set; } = string.Empty;
}

internal sealed class NativeKeyboardInteractiveResponsePayload
{
    public string[] Responses { get; set; } = Array.Empty<string>();
}
