namespace NovaTerminal.Models;

public sealed record RemoteSidebarEntry(
    string Name,
    string FullPath,
    bool IsDirectory);
