namespace NovaTerminal.Tests.Core;

public sealed class TabBehaviorTests
{
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
        Assert.Equal(440, margin.Right);
    }

    [Fact]
    public void GetTabHeaderViewportMargin_Mac_AddsLeftReservation()
    {
        var margin = NovaTerminal.MainWindow.GetTabHeaderViewportMargin(
            isMacOs: true,
            titleBarWidth: 0,
            titleBarRightMargin: 0);

        Assert.Equal(92, margin.Left);
        Assert.Equal(440, margin.Right);
    }

    [Fact]
    public void GetTabHeaderViewportMargin_GrowsRightReservationToFitTitleBarActions()
    {
        var margin = NovaTerminal.MainWindow.GetTabHeaderViewportMargin(
            isMacOs: false,
            titleBarWidth: 360,
            titleBarRightMargin: 140);

        Assert.Equal(0, margin.Left);
        Assert.Equal(516, margin.Right);
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
}
