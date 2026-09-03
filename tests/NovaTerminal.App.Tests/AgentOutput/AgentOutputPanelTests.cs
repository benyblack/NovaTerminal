using System;
using Avalonia.Controls;
using NovaTerminal.AgentOutput;
using Avalonia.Headless.XUnit;
using Xunit;

namespace NovaTerminal.Tests.AgentOutput;

/// <summary>
/// The panel view's contract with its view model: content swaps rebuild the markdown host, the
/// empty state and the rendered view are mutually exclusive, and the status line tracks
/// streaming. No pixels - the assist overlay tests already cover the "chrome but no content"
/// failure mode class, and these assertions pin the state machine that feeds it. AvaloniaFact,
/// not plain facts: the panel is a XAML UserControl, and InitializeComponent needs the headless
/// application up.
/// </summary>
public sealed class AgentOutputPanelTests
{
    private static AgentOutputPanel CreatePanel(out AgentOutputViewModel viewModel)
    {
        viewModel = new AgentOutputViewModel();
        var panel = new AgentOutputPanel();
        panel.SetViewModel(viewModel);
        return panel;
    }

    [AvaloniaFact]
    public void Initially_ShowsTheEmptyState()
    {
        var panel = CreatePanel(out _);

        Assert.True(panel.FindControl<StackPanel>("EmptyState").IsVisible);
        Assert.False(panel.FindControl<ScrollViewer>("ContentScroll").IsVisible);
        Assert.False(panel.FindControl<Button>("BtnCopyAll").IsEnabled);
    }

    [AvaloniaFact]
    public void ContentUpdate_SwapsEmptyStateForTheRenderedView()
    {
        var panel = CreatePanel(out var viewModel);

        viewModel.SetUpdate("# Heading\n\nparagraph", isStreaming: true);

        Assert.False(panel.FindControl<StackPanel>("EmptyState").IsVisible);
        Assert.True(panel.FindControl<ScrollViewer>("ContentScroll").IsVisible);
        Assert.True(panel.FindControl<Button>("BtnCopyAll").IsEnabled);
        Assert.NotEmpty(panel.FindControl<StackPanel>("MarkdownHost").Children);
    }

    [AvaloniaFact]
    public void StreamingStatus_ShowsInTheHeader_AndClearsWhenFinished()
    {
        var panel = CreatePanel(out var viewModel);
        var status = panel.FindControl<TextBlock>("StatusText");

        viewModel.SetUpdate("content", isStreaming: true);
        Assert.True(status.IsVisible);
        Assert.Equal("streaming…", status.Text);

        viewModel.SetUpdate("content", isStreaming: false);
        Assert.False(status.IsVisible);
    }

    [AvaloniaFact]
    public void ClearingTheContent_ReturnsToTheEmptyState()
    {
        var panel = CreatePanel(out var viewModel);
        viewModel.SetUpdate("## gone soon", isStreaming: false);

        viewModel.SetUpdate(string.Empty, isStreaming: false);

        Assert.True(panel.FindControl<StackPanel>("EmptyState").IsVisible);
        Assert.Empty(panel.FindControl<StackPanel>("MarkdownHost").Children);
    }

    [AvaloniaFact]
    public void IdenticalContentUpdate_DoesNotRebuildTheHost()
    {
        var panel = CreatePanel(out var viewModel);
        viewModel.SetUpdate("# stable", isStreaming: false);
        var host = panel.FindControl<StackPanel>("MarkdownHost");
        Control first = host.Children[0];

        // Same text, same hash: the tracker's dedupe contract means the host must not churn -
        // a streaming agent repaints constantly, and every rebuild is a layout pass.
        viewModel.SetUpdate("# stable", isStreaming: false);

        Assert.Same(first, host.Children[0]);
    }

    // ---------------------------------------------------------------- link scheme allowlist

    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("http://example.com/")]
    [InlineData("mailto:someone@example.com")]
    public void WebAndMailLinks_AreAllowedThroughTheAllowlist(string url)
    {
        Assert.True(AgentOutputPanel.IsSafeExternalUrl(url));
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("javascript:alert(1)")]
    [InlineData("../../etc/passwd")]
    [InlineData("")]
    public void ExecutablePaths_FileUrls_AndCustomSchemes_AreBlocked(string url)
    {
        // Agent output is untrusted: a crafted [label](target) must never name something the
        // shell handler would launch or hand to a protocol handler.
        Assert.False(AgentOutputPanel.IsSafeExternalUrl(url));
    }
}
