namespace NovaTerminal.Tests.Core;

public sealed class TabBehaviorTests
{
    [Theory]
    [InlineData(false, false, "None")]
    [InlineData(false, true, "OpenContextMenu")]
    [InlineData(true, false, "CloseTab")]
    [InlineData(true, true, "CloseTab")]
    public void ResolveTabHeaderPointerAction_MapsButtonsToExpectedAction(
        bool isMiddlePressed,
        bool isRightPressed,
        string expected)
    {
        var action = NovaTerminal.MainWindow.ResolveTabHeaderPointerAction(isMiddlePressed, isRightPressed);
        Assert.Equal(expected, action.ToString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShouldDeferTabContextMenuOpen_DefersOnlyWhenTabWasNotSelected(
        bool wasSelected,
        bool expected)
    {
        bool shouldDefer = NovaTerminal.MainWindow.ShouldDeferTabContextMenuOpen(wasSelected);
        Assert.Equal(expected, shouldDefer);
    }

    [Fact]
    public void GetNextMruIndex_Forward_CyclesAcrossAllTabs()
    {
        const int count = 6;
        int current = 0;

        int[] expected = { 1, 2, 3, 4, 5, 0 };
        foreach (int next in expected)
        {
            current = NovaTerminal.MainWindow.GetNextMruIndex(current, count, reverse: false);
            Assert.Equal(next, current);
        }
    }

    [Fact]
    public void GetNextMruIndex_Reverse_WrapsToTail()
    {
        int next = NovaTerminal.MainWindow.GetNextMruIndex(0, 4, reverse: true);
        Assert.Equal(3, next);
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(4, 4)]
    [InlineData(0, 1)]
    [InlineData(0, 0)]
    public void GetNextMruIndex_InvalidInputs_ReturnMinusOne(int selectedIndex, int count)
    {
        int next = NovaTerminal.MainWindow.GetNextMruIndex(selectedIndex, count, reverse: false);
        Assert.Equal(-1, next);
    }

    [Fact]
    public void CountHiddenTabs_ComputesExpectedHiddenCount()
    {
        int hidden = NovaTerminal.MainWindow.CountHiddenTabs(
            viewportWidth: 600,
            tabWidths: Enumerable.Repeat(120d, 20));

        Assert.Equal(15, hidden);
    }

    [Fact]
    public void CountHiddenTabs_UsesFallbackWidthWhenUnmeasured()
    {
        int hidden = NovaTerminal.MainWindow.CountHiddenTabs(
            viewportWidth: 250,
            tabWidths: new[] { 100d, 0d, -1d });

        // 100 + fallback120 fits; third fallback120 overflows.
        Assert.Equal(1, hidden);
    }

    [Fact]
    public void GetTabHeaderViewportMargin_NonMac_UsesOnlyRightReservation()
    {
        var margin = NovaTerminal.MainWindow.GetTabHeaderViewportMargin(
            isMacOs: false,
            titleBarWidth: 0,
            titleBarRightMargin: 0);

        Assert.Equal(0, margin.Left);
        Assert.Equal(NovaTerminal.MainWindow.MinimumTabHeaderRightReserve, margin.Right);
    }

    [Fact]
    public void GetTabHeaderViewportMargin_Mac_AddsLeftReservation()
    {
        var margin = NovaTerminal.MainWindow.GetTabHeaderViewportMargin(
            isMacOs: true,
            titleBarWidth: 0,
            titleBarRightMargin: 0);

        Assert.Equal(NovaTerminal.MainWindow.MacOsTrafficLightReserve, margin.Left);
        Assert.Equal(NovaTerminal.MainWindow.MinimumTabHeaderRightReserve, margin.Right);
    }

    [Fact]
    public void GetTabHeaderViewportMargin_GrowsRightReservationToFitTitleBarActions()
    {
        var margin = NovaTerminal.MainWindow.GetTabHeaderViewportMargin(
            isMacOs: false,
            titleBarWidth: 360,
            titleBarRightMargin: 140);

        Assert.Equal(0, margin.Left);
        double expectedRight = Math.Max(
            NovaTerminal.MainWindow.MinimumTabHeaderRightReserve,
            Math.Ceiling(360 + 140 + NovaTerminal.MainWindow.TabHeaderViewportPadding));
        Assert.Equal(expectedRight, margin.Right);
    }

    [Fact]
    public void GetTabHeaderViewportMargin_Mac_UsesActualTitleBarWidth_NotWindowsMinimum()
    {
        // On macOS the right caption-button reserve is collapsed (~8px), so the computed
        // right reservation can drop below the Windows-sized MinimumTabHeaderRightReserve.
        // The viewport must trust the actual measurement; otherwise a visible gap appears
        // between the last tab and the right-side custom buttons.
        var margin = NovaTerminal.MainWindow.GetTabHeaderViewportMargin(
            isMacOs: true,
            titleBarWidth: 340,
            titleBarRightMargin: 8);

        double expectedRight = Math.Ceiling(340 + 8 + NovaTerminal.MainWindow.TabHeaderViewportPadding);
        Assert.Equal(expectedRight, margin.Right);
        Assert.True(margin.Right < NovaTerminal.MainWindow.MinimumTabHeaderRightReserve);
    }

    [Fact]
    public void TruncateTabLabel_TruncatesWithEllipsis()
    {
        string value = NovaTerminal.MainWindow.TruncateTabLabel("abcdefgh", 6);
        Assert.Equal("abcde…", value);
    }

    [Fact]
    public void TruncateTabLabelWithSuffix_PreservesSuffixHint()
    {
        string value = NovaTerminal.MainWindow.TruncateTabLabelWithSuffix(
            "this-is-a-very-long-tab-title",
            maxLength: 12,
            suffix: "~ab12");

        Assert.EndsWith("~ab12", value);
        Assert.Equal(12, value.Length);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ShouldSkipTabWhenClosingOthers_SkipsPinnedOrProtectedTabs(
        bool isPinned,
        bool isProtected,
        bool expected)
    {
        bool skip = NovaTerminal.MainWindow.ShouldSkipTabWhenClosingOthers(isPinned, isProtected);
        Assert.Equal(expected, skip);
    }

    [Theory]
    [InlineData(false, "Pin Tab")]
    [InlineData(true, "Unpin Tab")]
    public void GetPinTabActionLabel_ReflectsPinnedState(bool isPinned, string expected)
    {
        string label = NovaTerminal.MainWindow.GetPinTabActionLabel(isPinned);
        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData(false, "Protect Tab")]
    [InlineData(true, "Unprotect Tab")]
    public void GetProtectTabActionLabel_ReflectsProtectedState(bool isProtected, string expected)
    {
        string label = NovaTerminal.MainWindow.GetProtectTabActionLabel(isProtected);
        Assert.Equal(expected, label);
    }
}
