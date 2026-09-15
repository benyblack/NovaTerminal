using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Models;

namespace Ntilde.Services.Ssh;

public interface IRemotePathAutocompleteService
{
    Task<IReadOnlyList<RemotePathSuggestion>> GetSuggestionsAsync(
        Guid profileId,
        Guid sessionId,
        string input,
        CancellationToken cancellationToken);
}
