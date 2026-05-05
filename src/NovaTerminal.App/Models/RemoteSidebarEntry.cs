using System;

namespace NovaTerminal.Models;

public sealed record RemoteSidebarEntry(
    string Name,
    string FullPath,
    bool IsDirectory)
{
    public DateTime? ModifiedAtUtc { get; init; }

    public bool Equals(RemoteSidebarEntry? other)
    {
        return other is not null &&
            string.Equals(Name, other.Name, StringComparison.Ordinal) &&
            string.Equals(FullPath, other.FullPath, StringComparison.Ordinal) &&
            IsDirectory == other.IsDirectory;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Name, FullPath, IsDirectory);
    }
}
