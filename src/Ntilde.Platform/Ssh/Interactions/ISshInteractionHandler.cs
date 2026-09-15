namespace NovaTerminal.Platform.Ssh.Interactions;

public interface ISshInteractionHandler
{
    Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken);
}
