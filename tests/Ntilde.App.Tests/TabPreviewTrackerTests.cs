using NovaTerminal.Shell;

public sealed class TabPreviewTrackerTests
{
    [Fact]
    public void FirstUpdate_ReturnsBottomMostNonEmptyRow()
    {
        var tracker = new TabPreviewTracker();

        string preview = tracker.Update(new[] { "hello", "", "world" });

        Assert.Equal("world", preview);
    }

    [Fact]
    public void FirstUpdate_EmptyScreen_ReturnsEmptyString()
    {
        var tracker = new TabPreviewTracker();

        string preview = tracker.Update(new[] { "", "", "" });

        Assert.Equal(string.Empty, preview);
    }

    [Fact]
    public void StaticBottomChrome_NewContentRowAbove_PreviewIsContentRow()
    {
        var tracker = new TabPreviewTracker();
        tracker.Update(new[] { "hello", "", "auto mode on (shift+tab to cycle)" });

        string preview = tracker.Update(new[]
        {
            "hello",
            "The agent replied with a new line",
            "auto mode on (shift+tab to cycle)",
        });

        Assert.Equal("The agent replied with a new line", preview);
    }

    [Fact]
    public void SpinnerOnlyChangeInBottomRow_PreviewStaysSticky()
    {
        var tracker = new TabPreviewTracker();
        // Long row, only the token count changes (< 40% of characters differ).
        const string previous = "Working... (esc to interrupt) · 3.4k tokens · 12s";
        const string current = "Working... (esc to interrupt) · 3.5k tokens · 12s";

        tracker.Update(new[] { "hello", "", previous });
        string preview = tracker.Update(new[] { "hello", "", current });

        // Nothing qualified as meaningfully changed, so the preview stays what it was —
        // which, on the FIRST update, was the bottom-most non-empty row (cold start).
        Assert.Equal(previous, preview);
    }

    [Fact]
    public void GenuineFullBottomRowChange_PlainShellCase_PreviewIsBottomRow()
    {
        var tracker = new TabPreviewTracker();
        tracker.Update(new[] { "", "", "" });

        string preview = tracker.Update(new[] { "", "", "$ cargo build" });

        Assert.Equal("$ cargo build", preview);
    }

    [Fact]
    public void RowCountShrink_DoesNotThrow_IndexBeyondOldCountsAsChanged()
    {
        var tracker = new TabPreviewTracker();
        tracker.Update(new[] { "a", "b", "c", "d" });

        string preview = tracker.Update(new[] { "x" });

        Assert.Equal("x", preview);
    }

    [Fact]
    public void RowCountGrow_DoesNotThrow_NewRowsCountAsChanged()
    {
        var tracker = new TabPreviewTracker();
        tracker.Update(new[] { "a" });

        string preview = tracker.Update(new[] { "a", "b", "new bottom row" });

        Assert.Equal("new bottom row", preview);
    }

    [Fact]
    public void NoRowQualifies_KeepsPreviousPreview()
    {
        var tracker = new TabPreviewTracker();
        tracker.Update(new[] { "hello", "", "status bar text here" });

        // Second update: bottom row unchanged, others empty -> nothing qualifies.
        string preview = tracker.Update(new[] { "hello", "", "status bar text here" });

        Assert.Equal("status bar text here", preview);
    }

    [Theory]
    [InlineData(null, "anything", true)]
    [InlineData("", "anything", true)]
    [InlineData("identical text here", "identical text here", false)]
    [InlineData("totally different content", "completely unlike the first one!", true)]
    public void IsMeaningfulChange_BoundaryCases(string? previous, string current, bool expected)
    {
        Assert.Equal(expected, TabPreviewTracker.IsMeaningfulChange(previous, current));
    }

    [Fact]
    public void IsMeaningfulChange_SpinnerGlyphOnly_IsBelowThreshold()
    {
        const string previous = "Working... (esc to interrupt) · 3.4k tokens · 12s";
        const string current = "Working... (esc to interrupt) · 3.5k tokens · 12s";

        Assert.False(TabPreviewTracker.IsMeaningfulChange(previous, current));
    }
}
