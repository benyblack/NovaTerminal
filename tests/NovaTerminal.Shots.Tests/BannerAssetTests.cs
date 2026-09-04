using System.Text.RegularExpressions;

namespace NovaTerminal.ShotsTests;

/// <summary>
/// The demo banner prints a version string into almost every published screenshot, and it is a
/// static string: the demo shell runs in an isolated world with no repository to read a real
/// version out of, so nothing at capture time can derive it. That made it the one piece of text
/// in the catalogue that could go stale without any scenario failing - and it did, sitting at
/// 0.5.0 while the product shipped 0.6.0 and then 0.7.0, across a full re-publish. The comment
/// in nova-banner.sh asking the next person to re-check it by hand was not enough, so this pins
/// it instead.
/// </summary>
public sealed class BannerAssetTests
{
    [Fact]
    public void BannerVersion_MatchesDirectoryBuildProps()
    {
        string banner = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "nova-banner.sh"));

        Match printed = Regex.Match(banner, @"NovaTerminal (?<version>\d+\.\d+\.\d+) \(");
        Assert.True(
            printed.Success,
            "nova-banner.sh no longer prints a 'NovaTerminal <x.y.z> (' line. If the banner's shape " +
            "changed deliberately, update this test's pattern - but do not delete the check: an " +
            "unpinned version is what let the banner advertise 0.5.0 two releases after the fact.");

        string propsPath = Path.Combine(RepositoryRoot(), "Directory.Build.props");
        Match declared = Regex.Match(File.ReadAllText(propsPath), @"<Version>(?<version>[^<]+)</Version>");
        Assert.True(declared.Success, $"No <Version> element in {propsPath}.");

        Assert.Equal(declared.Groups["version"].Value.Trim(), printed.Groups["version"].Value);
    }

    /// <summary>
    /// Walks up from the test binary until Directory.Build.props appears. Not hardcoded relative to
    /// AppContext.BaseDirectory, because that path depends on the TFM and configuration the suite
    /// happens to be running under.
    /// </summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
