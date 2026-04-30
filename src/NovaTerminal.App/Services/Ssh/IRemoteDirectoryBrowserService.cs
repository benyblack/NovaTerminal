using System;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.Models;

namespace NovaTerminal.Services.Ssh;

public interface IRemoteDirectoryBrowserService
{
    Task<RemoteSidebarListingResult> ListDirectoryAsync(
        Guid profileId,
        Guid sessionId,
        string remotePath,
        CancellationToken cancellationToken);
}
