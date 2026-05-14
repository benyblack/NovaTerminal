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

    [Fact]
    public void ResolveStartPath_TrimsDefaultRemoteDirectory_WhenCwdIsBlank()
    {
        string resolved = RemoteSidebarStartPathResolver.Resolve(
            currentWorkingDirectory: "   ",
            defaultRemoteDirectory: "  ~/downloads  ");

        Assert.Equal("~/downloads", resolved);
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
        Assert.NotSame(entries, result.Entries);
        Assert.Single(result.Entries);
        Assert.Equal("logs", result.Entries[0].Name);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ListingResultSuccess_TakesStableSnapshot_OfEntries()
    {
        RemoteSidebarEntry[] entries =
        {
            new("logs", "/srv/logs", true)
        };

        RemoteSidebarListingResult result = RemoteSidebarListingResult.Success("/srv", entries);
        entries[0] = new RemoteSidebarEntry("tmp", "/srv/tmp", true);

        Assert.Single(result.Entries);
        Assert.Equal("logs", result.Entries[0].Name);
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

    [Fact]
    public void ListingResultSuccess_Throws_WhenResolvedPathIsBlank()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => RemoteSidebarListingResult.Success("   ", Array.Empty<RemoteSidebarEntry>()));

        Assert.Equal("resolvedPath", exception.ParamName);
    }

    [Fact]
    public void ListingResultSuccess_Throws_WhenEntriesAreNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => RemoteSidebarListingResult.Success("/srv", entries: null!));

        Assert.Equal("entries", exception.ParamName);
    }

    [Fact]
    public void ListingResultFailure_Throws_WhenResolvedPathIsBlank()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => RemoteSidebarListingResult.Failure("   ", "permission denied"));

        Assert.Equal("resolvedPath", exception.ParamName);
    }

    [Fact]
    public void ListingResultFailure_Throws_WhenErrorMessageIsBlank()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => RemoteSidebarListingResult.Failure("/srv", "   "));

        Assert.Equal("errorMessage", exception.ParamName);
    }
}
