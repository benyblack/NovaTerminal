using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.Models;

public sealed class RemoteSidebarListingResult
{
    private RemoteSidebarListingResult(
        string resolvedPath,
        IReadOnlyList<RemoteSidebarEntry> entries,
        bool isSuccess,
        string? errorMessage)
    {
        ResolvedPath = resolvedPath;
        Entries = entries;
        IsSuccess = isSuccess;
        ErrorMessage = errorMessage;
    }

    public string ResolvedPath { get; }
    public IReadOnlyList<RemoteSidebarEntry> Entries { get; }
    public bool IsSuccess { get; }
    public string? ErrorMessage { get; }

    public static RemoteSidebarListingResult Success(
        string resolvedPath,
        IReadOnlyList<RemoteSidebarEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
        ArgumentNullException.ThrowIfNull(entries);

        RemoteSidebarEntry[] snapshot = entries.ToArray();

        return new RemoteSidebarListingResult(
            resolvedPath,
            snapshot,
            isSuccess: true,
            errorMessage: null);
    }

    public static RemoteSidebarListingResult Failure(
        string resolvedPath,
        string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        return new RemoteSidebarListingResult(
            resolvedPath,
            Array.Empty<RemoteSidebarEntry>(),
            isSuccess: false,
            errorMessage: errorMessage);
    }
}
