using System;
using NovaTerminal.Core;

namespace NovaTerminal.Models;

public sealed class TransferDialogRequest
{
    public TransferDirection Direction { get; init; }
    public TransferKind Kind { get; init; }
    public string RemotePath { get; init; } = string.Empty;
    public Guid ProfileId { get; init; }
    public Guid SessionId { get; init; }

    public static TransferDialogRequest ForAction(
        TransferDirection direction,
        TransferKind kind,
        string defaultRemotePath,
        Guid profileId,
        Guid sessionId)
    {
        return new TransferDialogRequest
        {
            Direction = direction,
            Kind = kind,
            RemotePath = defaultRemotePath,
            ProfileId = profileId,
            SessionId = sessionId
        };
    }
}
