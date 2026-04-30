using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class RemoteSidebarStartPathResolverTests
{
    [Fact]
    public void ResolveStartPath_PrefersPaneWorkingDirectory_OverProfileDefault()
    {
        string resolved = RemoteSidebarStartPathResolver.Resolve(
            currentWorkingDirectory: "/srv/app",
            defaultRemoteDirectory: "~/downloads");

        Assert.Equal("/srv/app", resolved);
    }

    [Fact]
    public void ResolveStartPath_TrimsWhitespace_FromSelectedSource()
    {
        string resolved = RemoteSidebarStartPathResolver.Resolve(
            currentWorkingDirectory: "  /srv/app  ",
            defaultRemoteDirectory: "  ~/downloads  ");

        Assert.Equal("/srv/app", resolved);
    }

    [Theory]
    [InlineData("   ", "~/downloads", "~/downloads")]
    [InlineData(null, "   ", "~")]
    public void ResolveStartPath_FallsBackFromBlankCwd_ToProfileDefault_ThenHome(
        string? currentWorkingDirectory,
        string? defaultRemoteDirectory,
        string expected)
    {
        string resolved = RemoteSidebarStartPathResolver.Resolve(
            currentWorkingDirectory,
            defaultRemoteDirectory);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ListingResultSuccess_ClearsError_AndPreservesEntries()
    {
        RemoteSidebarEntry[] entries =
        {
            new("logs", "/srv/logs", true)
        };

        RemoteSidebarListingResult result = RemoteSidebarListingResult.Success("/srv", entries);

        Assert.True(result.IsSuccess);
        Assert.Equal("/srv", result.ResolvedPath);
        Assert.Same(entries, result.Entries);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ListingResultFailure_CarriesError_AndReturnsEmptyEntries()
    {
        RemoteSidebarListingResult result = RemoteSidebarListingResult.Failure("/srv", "permission denied");

        Assert.False(result.IsSuccess);
        Assert.Equal("/srv", result.ResolvedPath);
        Assert.Empty(result.Entries);
        Assert.Equal("permission denied", result.ErrorMessage);
    }
}
