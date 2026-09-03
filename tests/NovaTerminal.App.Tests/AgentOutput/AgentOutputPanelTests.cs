using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
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

    // ---------------------------------------------------------------- fence rendering switch

    [AvaloniaFact]
    public void FenceSwitch_IsHidden_WhenTheResponseHasNoMarkdownFence()
    {
        var panel = CreatePanel(out var viewModel);

        viewModel.SetUpdate("# Just a heading\n\nno fences here\n", isStreaming: false);

        Assert.False(panel.FindControl<ToggleButton>("BtnRenderFences").IsVisible);
    }

    [AvaloniaFact]
    public void FenceSwitch_IsVisible_WhenTheResponseHasAMarkdownFence()
    {
        var panel = CreatePanel(out var viewModel);

        viewModel.SetUpdate("```markdown\n# Nested\n```\n", isStreaming: false);

        Assert.True(panel.FindControl<ToggleButton>("BtnRenderFences").IsVisible);
    }

    [AvaloniaFact]
    public void FenceSwitch_UncheckedThenNewContent_KeepsRenderingSource()
    {
        var panel = CreatePanel(out var viewModel);
        viewModel.SetUpdate("```markdown\n# First\n```\n", isStreaming: false);

        ToggleButton toggle = panel.FindControl<ToggleButton>("BtnRenderFences");
        toggle.IsChecked = false;
        toggle.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));

        // The choice lives on the view model, so the next content update must not undo it - that is
        // the whole reason the switch is panel-level rather than per-block.
        viewModel.SetUpdate("```markdown\n# Second\n```\n", isStreaming: false);

        Assert.False(viewModel.RenderFencedMarkdown);
        Assert.Contains(
            "# Second",
            panel.FindControl<StackPanel>("MarkdownHost").Children
                .OfType<Control>()
                .SelectMany(TextOf),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> TextOf(Control control)
    {
        // Markers set Text directly; paragraphs and fenced-source bodies carry Inlines instead
        // (see MarkdownRendererTests.TextOf) - both must be checked or raw fence source (which
        // goes through Inlines) is invisible to this helper.
        if (control is TextBlock block)
        {
            if (block.Text is { Length: > 0 } text)
            {
                yield return text;
            }
            else if (block.Inlines is { Count: > 0 } inlines)
            {
                var builder = new System.Text.StringBuilder();
                foreach (Inline inline in inlines)
                {
                    if (inline is Run run)
                    {
                        builder.Append(run.Text);
                    }
                }

                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                }
            }
        }

        if (control is Panel panel)
        {
            foreach (Control child in panel.Children)
            {
                foreach (string nested in TextOf(child))
                {
                    yield return nested;
                }
            }
        }

        if (control is Border { Child: Control inner })
        {
            foreach (string nested in TextOf(inner))
            {
                yield return nested;
            }
        }

        if (control is ContentControl { Content: Control content })
        {
            foreach (string nested in TextOf(content))
            {
                yield return nested;
            }
        }
    }
}
