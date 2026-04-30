using NovaTerminal.Models;

namespace NovaTerminal.ViewModels.Ssh;

public sealed class RemoteFilesSidebarEntryViewModel
{
    public RemoteFilesSidebarEntryViewModel(RemoteSidebarEntry entry)
    {
        Name = entry.Name;
        FullPath = entry.FullPath;
        IsDirectory = entry.IsDirectory;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
}
