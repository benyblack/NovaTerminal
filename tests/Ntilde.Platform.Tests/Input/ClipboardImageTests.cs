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

    [Fact]
    public void ToWslMountPath_WindowsDrivePath_MapsToMntWithLowercaseDrive()
    {
        string bs = ((char)92).ToString(); // backslash, avoiding escaping in the literal
        string windows = "C:" + bs + "Users" + bs + "me" + bs + "nova-clip-x.png";

        Assert.Equal("/mnt/c/Users/me/nova-clip-x.png", ClipboardImage.ToWslMountPath(windows));
    }

    [Fact]
    public void ToWslMountPath_LowercasesDriveLetter()
    {
        string bs = ((char)92).ToString();

        Assert.Equal("/mnt/d/tmp/a.png", ClipboardImage.ToWslMountPath("D:" + bs + "tmp" + bs + "a.png"));
    }

    [Fact]
    public void ToWslMountPath_NonDrivePath_ConvertsSeparatorsOnly()
    {
        string bs = ((char)92).ToString();

        Assert.Equal("/tmp/a.png", ClipboardImage.ToWslMountPath("/tmp/a.png"));
        Assert.Equal("relative/a.png", ClipboardImage.ToWslMountPath("relative" + bs + "a.png"));
    }

    [Fact]
    public void CleanUpOldTempImages_DeletesOldFilesButKeepsRecentOnes()
    {
        string oldFile = ClipboardImage.GetTempImagePath(".png");
        string recentFile = ClipboardImage.GetTempImagePath(".png");
        System.IO.File.WriteAllBytes(oldFile, new byte[] { 1 });
        System.IO.File.WriteAllBytes(recentFile, new byte[] { 1 });
        System.IO.File.SetLastWriteTimeUtc(oldFile, System.DateTime.UtcNow.AddHours(-48));

        try
        {
            ClipboardImage.CleanUpOldTempImages(System.TimeSpan.FromHours(24));

            Assert.False(System.IO.File.Exists(oldFile));
            Assert.True(System.IO.File.Exists(recentFile));
        }
        finally
        {
            if (System.IO.File.Exists(oldFile)) System.IO.File.Delete(oldFile);
            if (System.IO.File.Exists(recentFile)) System.IO.File.Delete(recentFile);
        }
    }
}
