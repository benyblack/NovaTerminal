using System.Collections.Generic;

namespace NovaTerminal.Models;

public sealed record RemoteSidebarListingResult(
    string ResolvedPath,
    IReadOnlyList<RemoteSidebarEntry> Entries,
    bool IsSuccess,
    string? ErrorMessage);
