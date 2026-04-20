using NovaTerminal.Core.Ssh.Interactions;

namespace NovaTerminal.Core.Tests.Ssh;

internal sealed class NativeSshTestInteractionHandler : ISshInteractionHandler
{
    private readonly string _password;

    public NativeSshTestInteractionHandler(string password)
    {
        _password = password;
    }

    public List<SshInteractionRequest> Requests { get; } = new();

    public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(request.Kind switch
        {
            SshInteractionKind.UnknownHostKey or SshInteractionKind.ChangedHostKey => SshInteractionResponse.AcceptHostKey(),
            SshInteractionKind.Password => SshInteractionResponse.FromSecret(_password),
            _ => throw new InvalidOperationException($"Unexpected native SSH interaction kind '{request.Kind}' in Docker E2E test.")
        });
    }
}
