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

    // ---- Display-label truncation (ResolveTabDisplayLabels, the pure core of
    // BuildTabDisplayLabels) ----
    //
    // includeMarkers:false is the vertical-header call: attention renders as trailing
    // chips there, so the marker suffix must be neither appended nor reserved while
    // truncating; the pinned/protected prefixes live in the base label and stay, and
    // the ~id collision disambiguation keeps working.

    private sealed record LabelTab(string Base, string Marker, string Hint);

    private static Dictionary<LabelTab, string> ResolveLabels(
        IReadOnlyList<LabelTab> tabs, int maxLength, bool includeMarkers)
        => NovaTerminal.MainWindow.ResolveTabDisplayLabels<LabelTab>(
            tabs, t => t.Base, t => t.Marker, t => t.Hint, maxLength, includeMarkers);

    [Fact]
    public void ResolveTabDisplayLabels_IncludeMarkersFalse_OmitsMarkerSuffix()
    {
        var tab = new List<LabelTab> { new("build", " 🔔", "~a1b2") };

        var withoutMarkers = ResolveLabels(tab, maxLength: 44, includeMarkers: false);
        var withMarkers = ResolveLabels(tab, maxLength: 44, includeMarkers: true);

        Assert.Equal("build", withoutMarkers[tab[0]]);
        Assert.Equal("build 🔔", withMarkers[tab[0]]);
    }

    [Fact]
    public void ResolveTabDisplayLabels_IncludeMarkersFalse_PreservesPrefixes()
    {
        // Prefixes (pinned/protected, and the forwarding badge) are part of the base
        // label, not the marker suffix — they must survive in both modes.
        var tab = new List<LabelTab> { new("📌 🔒 deploy 🔁 2", string.Empty, "~a1b2") };

        Assert.Equal("📌 🔒 deploy 🔁 2", ResolveLabels(tab, 44, includeMarkers: false)[tab[0]]);
        Assert.Equal("📌 🔒 deploy 🔁 2", ResolveLabels(tab, 44, includeMarkers: true)[tab[0]]);
    }

    [Fact]
    public void ResolveTabDisplayLabels_IncludeMarkersFalse_LongTitle_TruncatesWithoutReservingMarkerSpace()
    {
        string longTitle = new string('t', 60);
        var tab = new List<LabelTab> { new(longTitle, " 🔔", "~a1b2") };

        var vertical = ResolveLabels(tab, maxLength: 44, includeMarkers: false)[tab[0]];
        // Plain TruncateTabLabel shape: 43 chars + ellipsis, no marker room reserved.
        Assert.Equal(longTitle.Substring(0, 43) + "…", vertical);
        Assert.DoesNotContain("🔔", vertical);

        var horizontal = ResolveLabels(tab, maxLength: 44, includeMarkers: true)[tab[0]];
        // Marker mode truncates the title harder and keeps the suffix visible.
        Assert.EndsWith("🔔", horizontal);
        Assert.True(horizontal.Length <= 44);
    }

    [Fact]
    public void ResolveTabDisplayLabels_IncludeMarkersFalse_CollisionStillAppendsHint()
    {
        var tabs = new List<LabelTab>
        {
            new("same", " 🔔", "~a1b2"),
            new("same", string.Empty, "~c3d4"),
        };

        var vertical = ResolveLabels(tabs, maxLength: 44, includeMarkers: false);

        // Identical bases collide once the marker is dropped; each gets its own hint.
        Assert.Equal("same~a1b2", vertical[tabs[0]]);
        Assert.Equal("same~c3d4", vertical[tabs[1]]);
        Assert.DoesNotContain("🔔", vertical[tabs[0]]);

        // Marker mode: the different suffixes already disambiguate, so no hint is added.
        var horizontal = ResolveLabels(tabs, maxLength: 44, includeMarkers: true);
        Assert.Equal("same 🔔", horizontal[tabs[0]]);
        Assert.Equal("same", horizontal[tabs[1]]);
    }

    [Fact]
    public void ResolveTabDisplayLabels_IncludeMarkersTrue_CollisionPutsHintAfterMarker()
    {
        var tabs = new List<LabelTab>
        {
            new("same", " 🔔", "~a1b2"),
            new("same", " 🔔", "~c3d4"),
        };

        var labels = ResolveLabels(tabs, maxLength: 44, includeMarkers: true);

        Assert.Equal("same 🔔~a1b2", labels[tabs[0]]);
        Assert.Equal("same 🔔~c3d4", labels[tabs[1]]);
    }
}
