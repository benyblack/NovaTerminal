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
}
