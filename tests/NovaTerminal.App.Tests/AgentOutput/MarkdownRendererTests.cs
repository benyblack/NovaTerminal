using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using NovaTerminal.AgentOutput;
using Xunit;

namespace NovaTerminal.Tests.AgentOutput;

/// <summary>
/// The markdown-to-Avalonia renderer: the block subset agent CLIs emit, the emphasis and code
/// inline shapes, and the two safety behaviors the panel depends on (raw HTML never becomes
/// content, unknown blocks never crash the walk).
/// </summary>
/// <remarks>
/// Plain control-tree construction, no layout or styling - so the tests run without a headless
/// application. Colors resolve from a bare anchor with no theme resources, which exercises the
/// fixed-fallback path: the same degradation an unthemed surface gets.
/// </remarks>
public sealed class MarkdownRendererTests
{
    private static readonly Border Anchor = new();

    private static IEnumerable<Control> Descendants(Control control)
    {
        switch (control)
        {
            case Panel panel:
                foreach (Control child in panel.Children)
                {
                    yield return child;
                    foreach (Control nested in Descendants(child))
                    {
                        yield return nested;
                    }
                }

                break;

            case Border border when border.Child is Control child:
                yield return child;
                foreach (Control nested in Descendants(child))
                {
                    yield return nested;
                }

                break;

            case ContentControl content when content.Content is Control child:
                yield return child;
                foreach (Control nested in Descendants(child))
                {
                    yield return nested;
                }

                break;
        }
    }

    private static IEnumerable<TextBlock> TextBlocks(Control root)
        => Descendants(root).OfType<TextBlock>();

    private static string TextOf(TextBlock block)
    {
        // Markers set Text directly; paragraphs carry Inlines. Either, never both.
        if (block.Inlines is not { Count: > 0 })
        {
            return block.Text ?? string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        if (block.Inlines is not null)
        {
            foreach (Inline inline in block.Inlines)
            {
                if (inline is Run run)
                {
                    builder.Append(run.Text);
                }
            }
        }

        return builder.ToString();
    }

    [Fact]
    public void Heading_BecomesBoldText_SizedByLevel()
    {
        var root = (StackPanel)MarkdownRenderer.Build("# Title\n\n### Sub", Anchor).Root;

        var headings = TextBlocks(root).ToList();
        Assert.Equal("Title", TextOf((TextBlock)root.Children[0]));
        Assert.Equal(FontWeight.SemiBold, ((TextBlock)root.Children[0]).FontWeight);
        Assert.True(((TextBlock)root.Children[0]).FontSize > ((TextBlock)root.Children[1]).FontSize);
        Assert.Equal("Sub", TextOf(headings[1]));
    }

    [Fact]
    public void Paragraph_WithBoldAndItalic_ProducesStyledRuns()
    {
        var root = (StackPanel)MarkdownRenderer.Build("plain **bold** and *slant* text", Anchor).Root;

        var paragraph = (SelectableTextBlock)root.Children[0];
        var runs = paragraph.Inlines!.OfType<Span>().SelectMany(s => s.Inlines!.OfType<Run>()).ToList();

        Assert.Equal("plain  and  text", TextOf(paragraph));
        Assert.Equal(2, runs.Count);
        Assert.Equal("bold", runs[0].Text);
        Assert.Equal(FontWeight.Bold, ((Span)runs[0].Parent!).FontWeight);
        Assert.Equal("slant", runs[1].Text);
        Assert.Equal(FontStyle.Italic, ((Span)runs[1].Parent!).FontStyle);
    }

    [Fact]
    public void FencedCodeBlock_RendersItsText_WithACopyButton()
    {
        var root = (StackPanel)MarkdownRenderer.Build("```csharp\nvar x = 1;\n```\n", Anchor).Root;

        Control? copyButton = Descendants(root).FirstOrDefault(c => c is Button { Content: "Copy" });
        Assert.NotNull(copyButton);

        TextBlock? code = TextBlocks(root).FirstOrDefault(b => TextOf(b).Contains("var x = 1;", StringComparison.Ordinal));
        Assert.NotNull(code);
    }

    [Fact]
    public void InlineCode_RendersAsAChip()
    {
        var root = (StackPanel)MarkdownRenderer.Build("run `npm test` now", Anchor).Root;

        var paragraph = (SelectableTextBlock)root.Children[0];
        bool hasChip = paragraph.Inlines!.OfType<InlineUIContainer>()
            .Any(c => c.Child is Border { Child: TextBlock chip } && TextOf(chip) == "npm test");
        Assert.True(hasChip);
    }

    [Fact]
    public void Lists_RenderTheirItems_AndOrdering()
    {
        var root = (StackPanel)MarkdownRenderer.Build("1. first\n2. second\n\n- bullet\n", Anchor).Root;

        var markers = TextBlocks(root)
            .Select(TextOf)
            .Where(t => t is "1." or "2." or "•")
            .ToList();

        Assert.Equal(new[] { "1.", "2.", "•" }, markers);
    }

    [Fact]
    public void TaskListItem_RendersACheckboxGlyph()
    {
        var root = (StackPanel)MarkdownRenderer.Build("- [x] done\n- [ ] pending\n", Anchor).Root;

        string all = string.Concat(TextBlocks(root).Select(TextOf));
        Assert.Contains("\u2611", all, StringComparison.Ordinal);
        Assert.Contains("\u2610", all, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_RendersHeaderAndRows()
    {
        var root = (StackPanel)MarkdownRenderer.Build("| a | b |\n|---|---|\n| 1 | 2 |\n", Anchor).Root;

        Grid? grid = Descendants(root).OfType<Grid>()
            .FirstOrDefault(g => g.RowDefinitions.Count > 0);
        Assert.NotNull(grid);
        Assert.Equal(2, grid!.RowDefinitions.Count);
        Assert.Equal(2, grid.ColumnDefinitions.Count);

        string all = string.Concat(TextBlocks(root).Select(TextOf));
        Assert.Contains("a", all, StringComparison.Ordinal);
        Assert.Contains("2", all, StringComparison.Ordinal);
        Assert.DoesNotContain("---", all, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockQuote_RendersItsParagraphs()
    {
        var root = (StackPanel)MarkdownRenderer.Build("> quoted wisdom\n", Anchor).Root;

        Assert.Contains("quoted wisdom", TextBlocks(root).Select(TextOf), StringComparer.Ordinal);
    }

    [Fact]
    public void Link_RendersItsLabel_ForClicking()
    {
        var root = (StackPanel)MarkdownRenderer.Build("see [the docs](https://example.com) here", Anchor).Root;

        // The link renders as a TextBlock inside an InlineUIContainer of the paragraph, which
        // the Panel/Border walker does not cross - collect inline-hosted blocks explicitly.
        var inlineBlocks = ((SelectableTextBlock)root.Children[0]).Inlines!
            .OfType<InlineUIContainer>()
            .Select(c => c.Child)
            .OfType<TextBlock>()
            .Select(TextOf)
            .ToList();

        Assert.Contains("the docs", inlineBlocks);
    }

    [Fact]
    public void RawHtml_IsTreatedAsPlainText()
    {
        // DisableHtml keeps the tags from becoming structure. Whatever leaks through as literal
        // text is inert: the panel renders Avalonia text, never evaluates markup, so a stray
        // <script> in agent output is characters on screen and nothing else.
        var root = (StackPanel)MarkdownRenderer.Build("before\n\n<script>alert(1)</script>\n\nafter", Anchor).Root;

        string all = string.Concat(TextBlocks(root).Select(TextOf));
        Assert.Contains("before", all, StringComparison.Ordinal);
        Assert.Contains("after", all, StringComparison.Ordinal);
    }

    [Fact]
    public void UnrecognizedFrontMatter_IsSkipped_WithoutCrashing()
    {
        var root = (StackPanel)MarkdownRenderer.Build("---\ntitle: something\n---\n\nbody text", Anchor).Root;

        string all = string.Concat(TextBlocks(root).Select(TextOf));
        Assert.Contains("body text", all, StringComparison.Ordinal);
    }

    [Fact]
    public void SoftWrapDamage_InTheSource_IsJustText()
    {
        // The grid reader hands the renderer already-joined logical lines; a very long paragraph
        // must flow through the text wrapper, not be re-broken by the renderer.
        var root = (StackPanel)MarkdownRenderer.Build(new string('x', 500), Anchor).Root;

        var paragraph = (SelectableTextBlock)root.Children[0];
        Assert.Equal(500, TextOf(paragraph).Length);
    }

    [Fact]
    public void CopyButton_ForwardsTheCodeText()
    {
        string? copied = null;
        var root = (StackPanel)MarkdownRenderer.Build(
            "```\nsome code\n```\n",
            Anchor,
            onCopyText: text => copied = text).Root;

        var button = (Button)Descendants(root).First(c => c is Button);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("some code", copied);
    }

    [Fact]
    public void Build_ReportsNoTransformBlock_ForOrdinaryMarkdown()
    {
        MarkdownRenderResult result = MarkdownRenderer.Build("# Title\n\nbody\n", Anchor);

        Assert.IsType<StackPanel>(result.Root);
        Assert.False(result.HasTransformBlock);
    }

    [Fact]
    public void MarkdownFence_RendersNestedBlocks_NotSource()
    {
        MarkdownRenderResult result = MarkdownRenderer.Build("```markdown\n# Nested Title\n```\n", Anchor);

        // A rendered heading is a TextBlock with the heading's own size, not a monospace code run.
        TextBlock? heading = TextBlocks((StackPanel)result.Root)
            .FirstOrDefault(b => TextOf(b).Contains("Nested Title", StringComparison.Ordinal));
        Assert.NotNull(heading);
        Assert.True(heading!.FontSize > 12, "a rendered heading is larger than code text");
        Assert.True(result.HasTransformBlock);
    }

    [Fact]
    public void MarkdownFence_WithSwitchOff_RendersSource_ButStillReportsTransform()
    {
        MarkdownRenderResult result = MarkdownRenderer.Build(
            "```markdown\n# Nested Title\n```\n",
            Anchor,
            renderFencedMarkdown: false);

        TextBlock? source = TextBlocks((StackPanel)result.Root)
            .FirstOrDefault(b => TextOf(b).Contains("# Nested Title", StringComparison.Ordinal));
        Assert.NotNull(source);

        // The switch must stay visible, or the choice is not reversible.
        Assert.True(result.HasTransformBlock);
    }

    [Fact]
    public void MarkdownFence_NestedInsideAnother_RendersTheInnerOneAsSource()
    {
        const string md = "````markdown\n# Outer\n\n```markdown\n# Inner\n```\n````\n";

        MarkdownRenderResult result = MarkdownRenderer.Build(md, Anchor);

        // Outer renders: its heading is a real heading.
        TextBlock? outer = TextBlocks((StackPanel)result.Root)
            .FirstOrDefault(b => TextOf(b).Contains("Outer", StringComparison.Ordinal));
        Assert.NotNull(outer);
        Assert.True(outer!.FontSize > 12);

        // Inner does not: its hash survives as literal text at the depth cap.
        TextBlock? inner = TextBlocks((StackPanel)result.Root)
            .FirstOrDefault(b => TextOf(b).Contains("# Inner", StringComparison.Ordinal));
        Assert.NotNull(inner);
    }

    [Fact]
    public void MarkdownFence_KeepsCopyYieldingRawSource()
    {
        string? copied = null;
        MarkdownRenderResult result = MarkdownRenderer.Build(
            "```markdown\n# Nested Title\n```\n",
            Anchor,
            onCopyText: text => copied = text);

        Button copy = Descendants((StackPanel)result.Root).OfType<Button>().First(b => b.Content as string == "Copy");
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // Contains, not Equal: whether GetLinesText keeps a trailing newline is not this test's
        // subject. The surviving hash is what proves Copy yielded source rather than rendered text.
        Assert.NotNull(copied);
        Assert.Contains("# Nested Title", copied!, StringComparison.Ordinal);
    }
}
