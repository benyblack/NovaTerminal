namespace NovaTerminal.Models;

public sealed class RemotePathSuggestion
{
    public RemotePathSuggestion(string displayName, string fullPath, bool isDirectory)
    {
        DisplayName = displayName;
        FullPath = fullPath;
        IsDirectory = isDirectory;
    }

    public string DisplayName { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string KindText => IsDirectory ? "DIR" : "FILE";
}
