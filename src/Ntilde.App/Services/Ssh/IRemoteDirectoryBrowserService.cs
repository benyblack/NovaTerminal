using System;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Models;

namespace Ntilde.Services.Ssh;

public interface IRemoteDirectoryBrowserService
{
    Task<RemoteSidebarListingResult> ListDirectoryAsync(
        Guid profileId,
        Guid sessionId,
        string remotePath,
        CancellationToken cancellationToken);
}
