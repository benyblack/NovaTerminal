using NovaTerminal.Platform.Ssh.Interactions;

namespace NovaTerminal.Platform.Tests.Ssh;

internal sealed class NativeSshTestInteractionHandler : ISshInteractionHandler
{
    private readonly string _password;
    private readonly string? _passphrase;
    private readonly string? _keyboardSecret;
    private readonly bool _acceptHostKeys;

    /// <param name="password">Answer for a password prompt.</param>
    /// <param name="passphrase">
    /// Answer for a private-key passphrase prompt. Left null, a passphrase prompt is a test failure —
    /// a test that did not expect one wants to hear about it rather than silently succeed.
    /// </param>
    /// <param name="keyboardSecret">
    /// Answer for every prompt in a keyboard-interactive challenge. Defaults to
    /// <paramref name="password"/>, which is what PAM asks for on this fixture's image.
    /// </param>
    /// <param name="acceptHostKeys">
    /// False cancels host-key prompts instead of accepting them, so a test can assert the backend
    /// fails closed on a refused key rather than continuing.
    /// </param>
    public NativeSshTestInteractionHandler(
        string password,
        string? passphrase = null,
        string? keyboardSecret = null,
        bool acceptHostKeys = true)
    {
        _password = password;
        _passphrase = passphrase;
        _keyboardSecret = keyboardSecret;
        _acceptHostKeys = acceptHostKeys;
    }

    public List<SshInteractionRequest> Requests { get; } = new();

    /// <summary>
    /// A locked copy of <see cref="Requests"/>. Tests that poll — waiting for a prompt to arrive, or
    /// asserting one never does — must not enumerate the live list: the session's own thread adds to it
    /// concurrently, which throws mid-enumeration rather than failing the assertion it was checking.
    /// </summary>
    public IReadOnlyList<SshInteractionRequest> RequestSnapshot()
    {
        lock (Requests)
        {
            return Requests.ToArray();
        }
    }

    public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(request.Kind switch
        {
            SshInteractionKind.UnknownHostKey or SshInteractionKind.ChangedHostKey =>
                _acceptHostKeys ? SshInteractionResponse.AcceptHostKey() : SshInteractionResponse.Cancel(),
            SshInteractionKind.Password => SshInteractionResponse.FromSecret(_password),
            SshInteractionKind.Passphrase => SshInteractionResponse.FromSecret(
                _passphrase ?? throw new InvalidOperationException(
                    "Native SSH asked for a key passphrase, but this test did not supply one.")),
            SshInteractionKind.KeyboardInteractive => BuildKeyboardResponse(request),
            _ => throw new InvalidOperationException($"Unexpected native SSH interaction kind '{request.Kind}' in Docker E2E test.")
        });
    }

    /// <summary>
    /// One answer per prompt the server actually sent. PAM commonly opens with an informational
    /// challenge carrying no prompts at all, and answering that with a secret it never asked for makes
    /// the exchange fail — so an empty challenge gets an empty response.
    /// </summary>
    private SshInteractionResponse BuildKeyboardResponse(SshInteractionRequest request)
    {
        string secret = _keyboardSecret ?? _password;
        if (request.KeyboardPrompts.Count == 0)
        {
            return SshInteractionResponse.FromKeyboardResponses();
        }

        return SshInteractionResponse.FromKeyboardResponses(
            request.KeyboardPrompts.Select(_ => secret).ToArray());
    }
}
