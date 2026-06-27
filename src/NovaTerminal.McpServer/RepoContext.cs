using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NovaTerminal.McpServer;

/// <summary>
/// Read-only, path-safe access to the NovaTerminal repository for the MCP Dev Companion.
/// Resolves the repo root once (env override or by walking up to the solution file) and
/// constrains all file reads to the <c>docs/</c> subtree — no traversal, no arbitrary FS access,
/// matching the server's read-only/local-only security model (#97).
/// </summary>
public sealed class RepoContext
{
    public const string RepoRootEnvVar = "NOVATERMINAL_REPO_ROOT";
    private const string SolutionFileName = "NovaTerminal.sln";

    /// <summary>Absolute path to the repo root, or null if it could not be located.</summary>
    public string? RepoRoot { get; }

    internal RepoContext(string? repoRoot) => RepoRoot = repoRoot;

    public static RepoContext Discover()
    {
        var fromEnv = System.Environment.GetEnvironmentVariable(RepoRootEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            return new RepoContext(Path.GetFullPath(fromEnv));
        }

        // Walk up from the executable location looking for the solution file.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return new RepoContext(dir.FullName);
            }
            dir = dir.Parent;
        }

        return new RepoContext(null);
    }

    public bool TryGetDocsDir(out string docsDir)
    {
        docsDir = RepoRoot is null ? string.Empty : Path.Combine(RepoRoot, "docs");
        return RepoRoot is not null && Directory.Exists(docsDir);
    }

    /// <summary>Lists doc files (relative to <c>docs/</c>) with the given extensions.</summary>
    public IReadOnlyList<string> ListDocs()
    {
        if (!TryGetDocsDir(out var docsDir)) return System.Array.Empty<string>();
        return Directory
            .EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(docsDir, p).Replace('\\', '/'))
            .OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads a doc by its path relative to <c>docs/</c>. Returns false (and a reason) if the repo
    /// is unavailable, the path escapes <c>docs/</c>, or the file does not exist.
    /// </summary>
    public bool TryReadDoc(string relativePath, out string content, out string error)
    {
        content = string.Empty;
        error = string.Empty;

        if (!TryGetDocsDir(out var docsDir))
        {
            error = "Repository docs directory could not be located. " +
                    $"Set the {RepoRootEnvVar} environment variable to the repo root.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "A document path is required.";
            return false;
        }

        // Resolve and confirm the result stays inside docs/ (block ../ traversal, absolute paths).
        // GetFullPath can throw on malformed client input (invalid chars, too long) — treat as a
        // rejected path rather than crashing the server.
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(docsDir, relativePath));
        }
        catch (System.Exception ex) when (ex is System.ArgumentException
            or System.IO.PathTooLongException or System.NotSupportedException)
        {
            error = "The provided path is invalid.";
            return false;
        }

        var docsPrefix = docsDir.EndsWith(Path.DirectorySeparatorChar)
            ? docsDir
            : docsDir + Path.DirectorySeparatorChar;
        // Windows/macOS file systems are case-insensitive, so use a platform-appropriate comparison
        // to avoid rejecting valid paths that differ only in casing (e.g. drive letter).
        var comparison = System.OperatingSystem.IsWindows() || System.OperatingSystem.IsMacOS()
            ? System.StringComparison.OrdinalIgnoreCase
            : System.StringComparison.Ordinal;
        if (!full.StartsWith(docsPrefix, comparison))
        {
            error = "Path is outside the docs/ directory and was rejected.";
            return false;
        }

        if (!File.Exists(full))
        {
            error = $"Document '{relativePath}' was not found under docs/.";
            return false;
        }

        content = File.ReadAllText(full);
        return true;
    }
}
