using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class PublisherTests
{
    [Fact]
    public void ResolveDestination_KeepsEveryAssetUnderDocsAssetsShots()
    {
        var asset = new ShotAsset("hero-single", 1, "/tmp/hero-single@2x.png", 2560, 1600,
            "hero-single", "abc1234", "win-x64", "2026-08-28T00:00:00Z");

        string destination = Publisher.ResolveDestination(asset, repositoryRoot: "/repo");

        // Path.Combine("/repo", ...) and Path.GetFullPath(...) disagree about root-relative
        // paths on Windows (the former yields "/repo\docs\...", the latter "<drive>:\repo\docs\...").
        // Normalising both sides through GetFullPath keeps the "stays under the assets directory"
        // assertion meaningful on every OS instead of only on the one where the two happen to match.
        string expectedPrefix = Path.GetFullPath(Path.Combine("/repo", "docs", "assets", "shots"));
        Assert.StartsWith(expectedPrefix, destination, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDestination_RejectsANameThatEscapesTheAssetDirectory()
    {
        var asset = new ShotAsset("../../etc/passwd", 1, "/tmp/x.png", 10, 10,
            "x", "abc1234", "win-x64", "2026-08-28T00:00:00Z");

        Assert.Throws<InvalidOperationException>(() =>
            Publisher.ResolveDestination(asset, repositoryRoot: "/repo"));
    }

    [Fact]
    public void Publish_CopiesTier3VariantsAndSkipsMasters()
    {
        (string repositoryRoot, string outputDirectory) = NewTempRoots();
        try
        {
            var run = new ShotRun(outputDirectory, scale: 2.0);

            WriteFile(Path.Combine(outputDirectory, "hero-single@2x.png"));
            run.Record(Asset("hero-single", tier: 1, file: Path.Combine(outputDirectory, "hero-single@2x.png")));

            WriteFile(Path.Combine(outputDirectory, "hero-single-readme.png"));
            run.Record(Asset("hero-single-readme", tier: 3, file: Path.Combine(outputDirectory, "hero-single-readme.png")));

            IReadOnlyList<string> published = Publisher.Publish(run, repositoryRoot);

            string assetsDirectory = Path.Combine(repositoryRoot, "docs", "assets", "shots");
            Assert.True(File.Exists(Path.Combine(assetsDirectory, "hero-single-readme.png")));
            Assert.False(File.Exists(Path.Combine(assetsDirectory, "hero-single.png")));
            Assert.False(File.Exists(Path.Combine(assetsDirectory, "hero-single@2x.png")));
            Assert.Equal(["docs/assets/shots/hero-single-readme.png".Replace('/', Path.DirectorySeparatorChar)], published);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void Publish_CopiesClipFilesButNotTheTier4PngMaster()
    {
        (string repositoryRoot, string outputDirectory) = NewTempRoots();
        try
        {
            var run = new ShotRun(outputDirectory, scale: 2.0);

            WriteFile(Path.Combine(outputDirectory, "clip-agent@2x.png"));
            WriteFile(Path.Combine(outputDirectory, "clip-agent.webm"));
            WriteFile(Path.Combine(outputDirectory, "clip-agent.gif"));
            run.Record(Asset("clip-agent", tier: 4, file: Path.Combine(outputDirectory, "clip-agent@2x.png")));

            IReadOnlyList<string> published = Publisher.Publish(run, repositoryRoot);

            string assetsDirectory = Path.Combine(repositoryRoot, "docs", "assets", "shots");
            Assert.True(File.Exists(Path.Combine(assetsDirectory, "clip-agent.webm")));
            Assert.True(File.Exists(Path.Combine(assetsDirectory, "clip-agent.gif")));
            Assert.False(File.Exists(Path.Combine(assetsDirectory, "clip-agent.png")));
            Assert.Equal(2, published.Count);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void Publish_SkipsAMissingClipFileWithoutThrowing()
    {
        // ffmpeg-absent runs keep frames but skip encoding (ShotContext.RecordAsync) - a Tier 4
        // master with no .webm/.gif sibling on disk must not fail the whole publish.
        (string repositoryRoot, string outputDirectory) = NewTempRoots();
        try
        {
            var run = new ShotRun(outputDirectory, scale: 2.0);

            WriteFile(Path.Combine(outputDirectory, "clip-agent@2x.png"));
            run.Record(Asset("clip-agent", tier: 4, file: Path.Combine(outputDirectory, "clip-agent@2x.png")));

            IReadOnlyList<string> published = Publisher.Publish(run, repositoryRoot);

            Assert.Empty(published);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static (string repositoryRoot, string outputDirectory) NewTempRoots()
    {
        string id = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "nova-shots-tests");
        string repositoryRoot = Path.Combine(root, $"repo-{id}");
        string outputDirectory = Path.Combine(root, $"out-{id}");
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(outputDirectory);
        return (repositoryRoot, outputDirectory);
    }

    private static void WriteFile(string path) => File.WriteAllBytes(path, [1, 2, 3]);

    private static ShotAsset Asset(string name, int tier, string file) => new(
        Name: name,
        Tier: tier,
        File: file,
        Width: 100,
        Height: 100,
        Scenario: name,
        Commit: "abc1234",
        Os: "win-x64",
        TimestampUtc: "2026-08-28T00:00:00Z");
}
