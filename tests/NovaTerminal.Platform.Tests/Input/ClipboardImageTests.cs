using NovaTerminal.Platform.Input;

namespace NovaTerminal.Platform.Tests.Input;

public sealed class ClipboardImageTests
{
    [Fact]
    public void GetTempImagePath_UsesTempDirectoryAndExtension()
    {
        string path = ClipboardImage.GetTempImagePath(".png");

        Assert.StartsWith(System.IO.Path.GetTempPath(), path);
        Assert.EndsWith(".png", path);
        Assert.Contains("nova-clip-", path);
    }

    [Fact]
    public void GetTempImagePath_ReturnsUniquePathsAcrossCalls()
    {
        Assert.NotEqual(
            ClipboardImage.GetTempImagePath(".png"),
            ClipboardImage.GetTempImagePath(".png"));
    }

    [Fact]
    public void QuotePathForInput_NoSpace_AppendsTrailingSpaceWithoutQuotes()
    {
        Assert.Equal("/tmp/shot.png ", ClipboardImage.QuotePathForInput("/tmp/shot.png"));
    }

    [Fact]
    public void QuotePathForInput_WithSpace_WrapsInDoubleQuotes()
    {
        string q = ((char)34).ToString();

        string result = ClipboardImage.QuotePathForInput("/tmp/my dir/shot.png");

        Assert.Equal(q + "/tmp/my dir/shot.png" + q + " ", result);
    }

    [Fact]
    public void QuotePathForInput_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ClipboardImage.QuotePathForInput(string.Empty));
    }
}
