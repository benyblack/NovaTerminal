using System;
using System.IO;
using System.Text.RegularExpressions;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Guards the one packaging invariant that can destroy user data.
///
/// Velopack installs to <c>%LocalAppData%\{packId}</c> and clears that directory as part of
/// installing. <c>AppPaths.RootDirectory</c> is <c>%LocalAppData%\NovaTerminal</c> and holds
/// settings.json, themes, workspaces, policy, recordings and ssh/profiles.json. Packing with
/// <c>--packId NovaTerminal</c> therefore aims the installer squarely at the user's own config
/// and deletes it on first install.
///
/// That is not a theoretical risk. It happened during the #91 spike and wiped config on two
/// machines before anyone noticed, which is why this is enforced by a gating test rather than
/// a comment in the workflow.
/// </summary>
public sealed class VelopackPackIdTests
{
    /// <summary>Mirrors <c>AppPaths.AppName</c>, which is private to the App assembly.</summary>
    private const string AppDataDirectoryName = "NovaTerminal";

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NovaTerminal.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private static string ReleaseWorkflow()
        => File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));

    private static string PackId()
    {
        Match m = Regex.Match(ReleaseWorkflow(), @"--packId\s+(?<id>[^\s`\r\n]+)");
        Assert.True(m.Success, "release.yml no longer contains a --packId argument. If Velopack " +
                               "packaging was removed, delete this test; otherwise restore the flag.");
        return m.Groups["id"].Value;
    }

    [Fact]
    public void PackId_must_not_collide_with_the_app_data_directory()
    {
        string packId = PackId();

        Assert.False(
            string.Equals(packId, AppDataDirectoryName, StringComparison.OrdinalIgnoreCase),
            $"release.yml packs with --packId '{packId}', which makes Velopack's install root " +
            $"the %LocalAppData% subdirectory named '{packId}' -- the same directory AppPaths " +
            "uses for user config. " +
            "The installer clears that directory, so this silently deletes the user's settings, " +
            "themes, workspaces, policy, recordings and SSH profiles. Use a distinct packId and " +
            "carry the display name with --packTitle instead.");
    }

    [Fact]
    public void PackTitle_keeps_the_user_visible_name()
    {
        // The packId is deliberately not the product name, so the friendly name has to come from
        // somewhere or the shortcut and Add/Remove Programs entry show "NovaTerminalApp".
        Assert.Matches(@"--packTitle\s+NovaTerminal\b", ReleaseWorkflow());
    }
}
