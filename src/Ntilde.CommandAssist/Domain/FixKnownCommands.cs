using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.CommandAssist.Domain;

/// <summary>
/// The small, hand-kept list of command names the typo corrector is allowed to propose, plus the
/// edit-distance machinery that proposes them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately small, and deliberately not the knowledge catalogue.</strong> V2 Phase 4
/// task 3 brings a tldr-derived catalogue of several hundred commands for the Help surface. This
/// is not that list and must not become it. A typo corrector's precision falls as its vocabulary
/// grows: every extra name is another chance that a real, working, locally-installed command sits
/// one edit away from something the user meant literally. The names here are ones whose
/// misspellings are common enough to be worth a high-confidence correction, and nothing else.
/// </para>
/// <para>
/// <strong>Distance rules.</strong> One edit for short names (<= 4 characters, where one edit is
/// already a large fraction of the word), two for longer ones. A correction is only offered when
/// the winner is unique at its distance - <c>cd</c> and <c>ls</c> are both one edit from <c>cs</c>,
/// and guessing between them is worse than saying nothing. Case is folded, because
/// <c>Get-Childitem</c> is not a typo.
/// </para>
/// </remarks>
public static class FixKnownCommands
{
    /// <summary>
    /// Names proposed as corrections. Grouped by origin so additions land in the right place.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        // POSIX core
        "ls", "cd", "cat", "cp", "mv", "rm", "mkdir", "rmdir", "pwd", "echo", "touch",
        "chmod", "chown", "ln", "find", "grep", "sed", "awk", "sort", "uniq", "head",
        "tail", "less", "more", "wc", "diff", "tar", "gzip", "gunzip", "zip", "unzip",
        "curl", "wget", "ssh", "scp", "rsync", "ping", "kill", "ps", "top", "htop",
        "df", "du", "which", "man", "sudo", "su", "export", "env", "history", "clear",
        "xargs", "tee", "nano", "vim", "vi", "nvim", "emacs", "tmux", "screen", "jq",

        // Version control
        "git", "gh", "svn", "hg",

        // Containers and orchestration
        "docker", "podman", "kubectl", "helm", "minikube", "compose",

        // Language toolchains and package managers
        "node", "npm", "npx", "pnpm", "yarn", "bun", "deno",
        "python", "python3", "pip", "pip3", "poetry", "uv",
        "dotnet", "nuget", "msbuild",
        "cargo", "rustc", "rustup",
        "go", "gofmt",
        "java", "javac", "mvn", "gradle",
        "ruby", "gem", "bundle", "rake",
        "php", "composer",

        // Build and infra
        "make", "cmake", "ninja", "terraform", "ansible", "vagrant", "aws", "az", "gcloud",

        // Windows
        "dir", "copy", "del", "move", "type", "cls", "where", "tasklist", "taskkill",
        "robocopy", "winget", "choco", "scoop",

        // PowerShell verbs people actually type
        "Get-ChildItem", "Set-Location", "Get-Content", "Set-Content", "Get-Command",
        "Get-Help", "Get-Process", "Stop-Process", "Test-Path", "New-Item", "Remove-Item",
        "Copy-Item", "Move-Item", "Select-String", "Invoke-WebRequest", "Invoke-RestMethod",
        "Write-Host", "Write-Output", "Start-Process", "Get-Service", "Measure-Object",
    ];

    /// <summary>
    /// The single best correction for <paramref name="token"/>, or null when there is no unique
    /// winner inside the allowed edit distance.
    /// </summary>
    /// <param name="token">The token the shell could not resolve.</param>
    /// <param name="distance">The edit distance to the returned name; 0 when nothing is returned.</param>
    public static string? TryCorrect(string token, out int distance)
    {
        distance = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string? best = null;
        int bestDistance = int.MaxValue;
        int bestCount = 0;

        foreach (string candidate in All)
        {
            int d = LevenshteinDistance(token, candidate);
            if (d > MaxAllowedDistance(token))
            {
                continue;
            }

            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
                bestCount = 1;
            }
            else if (d == bestDistance)
            {
                bestCount++;
            }
        }

        // Distance 0 means the token *is* a known command: the failure is not a typo, and
        // "did you mean git?" for `git` is the single most embarrassing thing this can print.
        if (best is null || bestDistance == 0 || bestCount > 1)
        {
            return null;
        }

        distance = bestDistance;
        return best;
    }

    /// <summary>Whether <paramref name="token"/> is a name on the list, ignoring case.</summary>
    public static bool IsKnown(string token)
        => All.Any(candidate => string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase));

    private static int MaxAllowedDistance(string token)
        => token.Length <= 4 ? 1 : 2;

    /// <summary>
    /// Optimal string alignment distance (restricted Damerau-Levenshtein) over the case-folded
    /// strings: insertion, deletion, substitution and <em>transposition of adjacent characters</em>
    /// each cost one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transposition term is not a refinement, it is the whole point. Transposing two adjacent
    /// keys is the most common typing error there is, and plain Levenshtein scores it as two edits
    /// - so <c>gti</c> is distance 2 from <c>git</c>, falls outside the one-edit budget a
    /// three-character token gets, and the single most recognisable typo in the world produces no
    /// suggestion. With this it is distance 1, which is also the honest description of what
    /// happened.
    /// </para>
    /// <para>
    /// Full matrix rather than rolling rows: the strings are command names, the caller runs this
    /// once per failing command, and the rolling-row form of the transposition term needs three
    /// rows and an index dance that is easy to get subtly wrong.
    /// </para>
    /// </remarks>
    public static int LevenshteinDistance(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        string left = source.ToLowerInvariant();
        string right = target.ToLowerInvariant();

        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        int[,] d = new int[left.Length + 1, right.Length + 1];
        for (int i = 0; i <= left.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= right.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            for (int j = 1; j <= right.Length; j++)
            {
                int substitution = left[i - 1] == right[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + substitution);

                if (i > 1 && j > 1 &&
                    left[i - 1] == right[j - 2] &&
                    left[i - 2] == right[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[left.Length, right.Length];
    }
}
