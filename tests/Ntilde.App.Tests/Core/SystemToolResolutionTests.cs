using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NovaTerminal.Pty;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Covers <see cref="RustPtySession.ResolveSystemTool"/>, which turns a helper name such as
/// <c>pgrep</c> into an absolute path without handing the lookup to the operating system's
/// implicit <c>PATH</c> search (csharpsquid:S4036).
/// </summary>
/// <remarks>
/// <para>
/// The rule is satisfied by any absolute path, so the first attempt at it hard-coded
/// <c>/usr/bin</c> with a <c>/bin</c> fallback. That is a list of the layouts we happened to think
/// of, and it is wrong on every prefix-based distribution: under Nix, Guix or a custom prefix
/// <c>pgrep</c> is on <c>PATH</c> and in neither directory, so the probe launched a path that does
/// not exist, swallowed the failure, and reported that the shell had no children.
/// </para>
/// <para>
/// That false negative is not cosmetic. <c>HasActiveChildProcesses</c> feeds
/// <c>ShouldAutoAcceptRunningPaneClose</c>, so a pane with a running child would close without the
/// confirmation prompt that exists to protect it. Hence the order pinned here: trusted system
/// directories first, so the answer does not depend on the environment where it does not have to,
/// and <c>PATH</c> only as the fallback that keeps those layouts working.
/// </para>
/// </remarks>
public sealed class SystemToolResolutionTests
{
    /// <summary>A filesystem probe over a fixed set of paths, separator-insensitive.</summary>
    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths.Select(Normalize), StringComparer.Ordinal);
        return candidate => set.Contains(Normalize(candidate));
    }

    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static string Path_(params string[] dirs) => string.Join(Path.PathSeparator, dirs);

    [Fact]
    public void PrefersTheTrustedSystemDirectory_OverPath()
    {
        var resolved = RustPtySession.ResolveSystemTool(
            "pgrep",
            Path_("/opt/custom/bin"),
            Existing("/usr/bin/pgrep", "/opt/custom/bin/pgrep"));

        Assert.Equal("/usr/bin/pgrep", Normalize(resolved!));
    }

    [Fact]
    public void FallsBackToBin_WhenUsrBinDoesNotHaveIt()
    {
        var resolved = RustPtySession.ResolveSystemTool(
            "pgrep",
            pathValue: null,
            Existing("/bin/pgrep"));

        Assert.Equal("/bin/pgrep", Normalize(resolved!));
    }

    /// <summary>
    /// The regression this method exists for: a prefix-based layout where the tool is on
    /// <c>PATH</c> and in neither trusted directory.
    /// </summary>
    [Fact]
    public void ResolvesFromPath_WhenNoTrustedDirectoryHasIt()
    {
        var resolved = RustPtySession.ResolveSystemTool(
            "pgrep",
            Path_("/run/current-system/sw/bin", "/nix/store/abc/bin"),
            Existing("/nix/store/abc/bin/pgrep"));

        Assert.Equal("/nix/store/abc/bin/pgrep", Normalize(resolved!));
    }

    /// <summary>
    /// A relative <c>PATH</c> entry is the hazard S4036 is actually about - it resolves against
    /// the working directory, so it must never become the executable we launch.
    /// </summary>
    [Fact]
    public void IgnoresRelativePathEntries()
    {
        var resolved = RustPtySession.ResolveSystemTool(
            "pgrep",
            Path_(".", "relative/bin"),
            Existing("pgrep", "relative/bin/pgrep"));

        Assert.Null(resolved);
    }

    [Fact]
    public void ReturnsNull_WhenTheToolIsNowhere()
    {
        var resolved = RustPtySession.ResolveSystemTool(
            "pgrep",
            Path_("/opt/bin"),
            Existing("/usr/bin/ls"));

        Assert.Null(resolved);
    }

    [Fact]
    public void SkipsUnusablePathEntries_InsteadOfFailingTheWholeProbe()
    {
        var resolved = RustPtySession.ResolveSystemTool(
            "pgrep",
            Path_("\0bad", "/opt/bin"),
            Existing("/opt/bin/pgrep"));

        Assert.Equal("/opt/bin/pgrep", Normalize(resolved!));
    }
}
