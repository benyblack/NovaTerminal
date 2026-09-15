using Ntilde.Shell;

namespace Ntilde.Tests.Core;

public sealed class WorkspaceBundleNamingTests
{
    [Theory]
    [InlineData(@"C:\x\dev.ntildews.json", "dev")]
    [InlineData(@"C:\x\dev.novaws.json", "dev")]
    [InlineData("/home/u/Team Setup.NOVAWS.JSON", "Team Setup")]
    [InlineData(@"C:\x\plain.json", "plain")]
    [InlineData(@"C:\x\odd.name.json", "odd.name")]
    public void SuggestedWorkspaceName_StripsEitherBundleExtension(string path, string expected)
    {
        Assert.Equal(expected, WorkspaceBundleNaming.SuggestedWorkspaceName(path));
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
