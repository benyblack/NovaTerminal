using System;
using System.Globalization;
using NovaTerminal.Models;

namespace NovaTerminal.ViewModels.Ssh;

public sealed class RemoteFilesSidebarEntryViewModel
{
    private const string DirectoryIconData = "M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8A2,2 0 0,0 20,6H12L10,4Z";
    private const string FileIconData = "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M14,9V3.5L19.5,9H14Z";

    public RemoteFilesSidebarEntryViewModel(RemoteSidebarEntry entry)
    {
        Name = entry.Name;
        FullPath = entry.FullPath;
        IsDirectory = entry.IsDirectory;
        ModifiedDisplayText = FormatModifiedDisplayText(entry.ModifiedAtUtc);
        EntryIconData = entry.IsDirectory ? DirectoryIconData : FileIconData;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string ModifiedDisplayText { get; }
    public string EntryIconData { get; }

    private static string FormatModifiedDisplayText(DateTime? modifiedAtUtc)
    {
        return modifiedAtUtc?.ToString("MMM dd", CultureInfo.InvariantCulture) ?? "-";
    }
}
