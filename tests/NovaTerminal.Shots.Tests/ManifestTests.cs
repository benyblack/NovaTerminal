using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class ManifestTests
{
    private static string NewManifestPath() => Path.Combine(
        Path.GetTempPath(),
        "nova-shots-tests",
        Guid.NewGuid().ToString("N"),
        "shots.json");

    private static ShotAsset Asset(string name) => new(
        Name: name,
        Tier: 1,
        File: $@"C:\artifacts\shots\{name}@2x.png",
        Width: 2560,
        Height: 1600,
        Scenario: name,
        Commit: "abc1234",
        Os: "win-x64",
        TimestampUtc: "2026-08-28T09:15:00.0000000Z");

    [Fact]
    public void Write_ThenRead_RoundTripsEveryField()
    {
        string path = NewManifestPath();

        try
        {
            Manifest.Write(path, [Asset("hero-single")]);

            ShotAsset asset = Assert.Single(Manifest.Read(path));
            Assert.Equal(Asset("hero-single"), asset);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Write_CreatesTheOutputDirectory()
    {
        // The output directory is artifacts/shots, which is gitignored and so absent on a fresh
        // clone. Write has to create it rather than fail the run after the images were captured.
        string path = NewManifestPath();

        try
        {
            Manifest.Write(path, [Asset("hero-single"), Asset("hero-split")]);

            Assert.True(File.Exists(path), $"Expected a manifest at {path}.");
            Assert.Equal(2, Manifest.Read(path).Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Write_OverwritesAPreviousManifest()
    {
        // Every run rewrites the manifest in place. A merge of the old and new lists would claim
        // assets a later run no longer produces.
        string path = NewManifestPath();

        try
        {
            Manifest.Write(path, [Asset("hero-single"), Asset("hero-split")]);
            Manifest.Write(path, [Asset("hero-single")]);

            Assert.Equal(["hero-single"], Manifest.Read(path).Select(a => a.Name));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
