namespace NovaTerminal.Core.Ssh.Native;

public sealed class NativeRemotePathEntry
{
    public NativeRemotePathEntry(string name, string fullPath, bool isDirectory)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
}
