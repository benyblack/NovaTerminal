namespace NovaTerminal.Tests.Core;

public sealed class TabTitleResolutionTests
{
    [Fact]
    public void ResolveTabPrimaryTitle_PrefersUserTitle()
    {
        string result = NovaTerminal.MainWindow.ResolveTabPrimaryTitle(
            userTitle: "My Session",
            paneBaseTitle: "bash · repo",
            fallbackHeader: "decorated 🔔 •");

        Assert.Equal("My Session", result);
    }

    [Fact]
    public void ResolveTabPrimaryTitle_UsesPaneBaseTitleWhenUserTitleMissing()
    {
        string result = NovaTerminal.MainWindow.ResolveTabPrimaryTitle(
            userTitle: null,
            paneBaseTitle: "bash · repo",
            fallbackHeader: "decorated 🔔 •");

        Assert.Equal("bash · repo", result);
    }

    [Fact]
    public void ResolveTabPrimaryTitle_FallsBackToHeaderThenTerminal()
    {
        string fromHeader = NovaTerminal.MainWindow.ResolveTabPrimaryTitle(
            userTitle: "",
            paneBaseTitle: "   ",
            fallbackHeader: "Header Title");
        Assert.Equal("Header Title", fromHeader);

        string defaultTitle = NovaTerminal.MainWindow.ResolveTabPrimaryTitle(
            userTitle: null,
            paneBaseTitle: null,
            fallbackHeader: null);
        Assert.Equal("Terminal", defaultTitle);
    }
}
