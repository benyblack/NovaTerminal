using System;

namespace NovaTerminal.Platform.Ssh.Native;

public sealed class NativeRemotePathEntry
{
    public NativeRemotePathEntry(string name, string fullPath, bool isDirectory, DateTime? modifiedAtUtc = null)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        ModifiedAtUtc = NormalizeModifiedAtUtc(modifiedAtUtc);
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public DateTime? ModifiedAtUtc { get; }

    private static DateTime? NormalizeModifiedAtUtc(DateTime? modifiedAtUtc)
    {
        if (!modifiedAtUtc.HasValue)
        {
            return null;
        }

        DateTime value = modifiedAtUtc.Value;
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
