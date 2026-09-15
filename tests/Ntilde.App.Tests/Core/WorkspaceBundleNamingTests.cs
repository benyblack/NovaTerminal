using Ntilde.Shell;

namespace Ntilde.Tests.Core;

public sealed class WorkspaceBundleNamingTests
{
    [Theory]
    [InlineData("dev.ntildews.json", "dev")]
    [InlineData("dev.novaws.json", "dev")]
    [InlineData("/home/u/Team Setup.NOVAWS.JSON", "Team Setup")]
    [InlineData("plain.json", "plain")]
    [InlineData("odd.name.json", "odd.name")]
    public void SuggestedWorkspaceName_StripsEitherBundleExtension(string fileName, string expected)
    {
        // Rows that are already a full path (forward slashes work on every OS)
        // are used as-is; bare file names are anchored under a temp directory
        // so this stays portable to Linux CI, where a backslash is just a
        // regular character rather than a path separator.
        string path = fileName.Contains('/')
            ? fileName
            : Path.Combine(Path.GetTempPath(), fileName);

        Assert.Equal(expected, WorkspaceBundleNaming.SuggestedWorkspaceName(path));
    }

    [Fact]
    public void SuggestedWorkspaceName_StripsExtension_ForNestedDirectoryPath()
    {
        string path = Path.Combine("dir", "sub", "x.novaws.json");

        Assert.Equal("x", WorkspaceBundleNaming.SuggestedWorkspaceName(path));
    }

    [Fact]
    public void SuggestedFileName_UsesNewExtensionAndTrims()
    {
        Assert.Equal("dev.ntildews.json", WorkspaceBundleNaming.SuggestedFileName("  dev "));
    }

    [Fact]
    public void PickerPatterns_ListNewThenLegacyThenJson()
    {
        Assert.Equal(new[] { "*.ntildews.json", "*.novaws.json", "*.json" }, WorkspaceBundleNaming.PickerPatterns);
    }
}
