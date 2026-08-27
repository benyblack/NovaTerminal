using System;

namespace NovaTerminal.Services.Ssh;

public static class RemoteSidebarStartPathResolver
{
    public static string Resolve(string? currentWorkingDirectory, string? defaultRemoteDirectory)
    {
        if (!string.IsNullOrWhiteSpace(currentWorkingDirectory))
        {
            return NormalizeUncStylePath(currentWorkingDirectory.Trim());
        }

        if (!string.IsNullOrWhiteSpace(defaultRemoteDirectory))
        {
            return defaultRemoteDirectory.Trim();
        }

        return "~";
    }

    /// <summary>
    /// A cwd tracked from OSC 7 on a remote SSH host renders as a Windows UNC path
    /// (<c>\\host\dir</c>) when the shell's reported hostname differs from this machine's own -
    /// see the "local-authority carve-out" remarks on <c>AnsiParser.TryExtractPathFromOsc7</c>.
    /// The SFTP session is already connected to that exact host, so the UNC "host" segment is
    /// redundant; every consumer of a sidebar path - the initial directory, the "jump to cwd"
    /// comparison, and the native SFTP list call - needs the POSIX path underneath it instead,
    /// so this normalization has to run identically wherever a raw OSC7-derived path is read
    /// (Codex review, PR #351: the sidebar's own jump-target comparison regressed by comparing
    /// an un-normalized cwd against an already-normalized <c>CurrentPath</c>).
    /// </summary>
    public static string NormalizeUncStylePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return path;
        }

        string withoutHostPrefix = path[2..];
        int separatorIndex = withoutHostPrefix.IndexOf('\\');
        string remainder = separatorIndex < 0 ? string.Empty : withoutHostPrefix[(separatorIndex + 1)..];
        return "/" + remainder.Replace('\\', '/');
    }
}
