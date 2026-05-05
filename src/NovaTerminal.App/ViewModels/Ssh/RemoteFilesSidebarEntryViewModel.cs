using System;
using System.Globalization;
using NovaTerminal.Models;

namespace NovaTerminal.ViewModels.Ssh;

public sealed class RemoteFilesSidebarEntryViewModel
{
    public RemoteFilesSidebarEntryViewModel(RemoteSidebarEntry entry)
    {
        Name = entry.Name;
        FullPath = entry.FullPath;
        IsDirectory = entry.IsDirectory;
        ModifiedDisplayText = FormatModifiedDisplayText(entry.ModifiedAtUtc);
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string ModifiedDisplayText { get; }

    private static string FormatModifiedDisplayText(DateTime? modifiedAtUtc)
    {
        return modifiedAtUtc?.ToString("MMM dd", CultureInfo.InvariantCulture) ?? "-";
    }
}
