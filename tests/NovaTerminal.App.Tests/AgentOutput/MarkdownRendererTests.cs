using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using NovaTerminal.AgentOutput;
using Avalonia.Headless.XUnit;
using Xunit;

namespace NovaTerminal.Tests.AgentOutput;

/// <summary>
/// The markdown-to-Avalonia renderer: the block subset agent CLIs emit, the emphasis and code
/// inline shapes, and the two safety behaviors the panel depends on (raw HTML never becomes
/// content, unknown blocks never crash the walk).
/// </summary>
/// <remarks>
/// Control-tree construction only - no layout, no styling. Colors resolve from a bare anchor with
/// no theme resources, which exercises the fixed-fallback path: the same degradation an unthemed
/// surface gets.
///
/// <para>
/// <b><see cref="AvaloniaFactAttribute"/>, not <c>[Fact]</c>, and the distinction is load-bearing.</b>
/// These tests build real Avalonia visuals, and Avalonia's ambient state - the media context most
/// of all - has thread affinity to the headless session's dispatch thread. Constructing visuals
/// from a plain <c>[Fact]</c> touches that state from a thread that owns none of it and corrupts it
/// for every <c>[AvaloniaFact]</c> suite sharing the process, which then fails with "The calling
/// thread cannot access this object because a different thread owns it" - non-deterministically,
/// in unrelated classes, depending on test order. That is what turned main red after #397: the
/// tab strip's pointer-driven tests died in a run whose only change was more tests here.
/// <c>AvaloniaBootLocatorHygieneTests.TheAmbientMediaContextBelongsToTheSessionDispatchThread</c>
/// is the guard for it. The session costs these tests a few seconds; do not trade it back.
/// </para>
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

    [AvaloniaFact]
    public void Heading_BecomesBoldText_SizedByLevel()
    {
        var root = (StackPanel)MarkdownRenderer.Build("# Title\n\n### Sub", Anchor).Root;

        var headings = TextBlocks(root).ToList();
        Assert.Equal("Title", TextOf((TextBlock)root.Children[0]));
        Assert.Equal(FontWeight.SemiBold, ((TextBlock)root.Children[0]).FontWeight);
        Assert.True(((TextBlock)root.Children[0]).FontSize > ((TextBlock)root.Children[1]).FontSize);
        Assert.Equal("Sub", TextOf(headings[1]));
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
    public void FencedCodeBlock_RendersItsText_WithACopyButton()
    {
        var root = (StackPanel)MarkdownRenderer.Build("```csharp\nvar x = 1;\n```\n", Anchor).Root;

        Control? copyButton = Descendants(root).FirstOrDefault(c => c is Button { Content: "Copy" });
        Assert.NotNull(copyButton);

        TextBlock? code = TextBlocks(root).FirstOrDefault(b => TextOf(b).Contains("var x = 1;", StringComparison.Ordinal));
        Assert.NotNull(code);
    }

    [AvaloniaFact]
    public void InlineCode_RendersAsAChip()
    {
        var root = (StackPanel)MarkdownRenderer.Build("run `npm test` now", Anchor).Root;

        var paragraph = (SelectableTextBlock)root.Children[0];
        bool hasChip = paragraph.Inlines!.OfType<InlineUIContainer>()
            .Any(c => c.Child is Border { Child: TextBlock chip } && TextOf(chip) == "npm test");
        Assert.True(hasChip);
    }

    [AvaloniaFact]
    public void Lists_RenderTheirItems_AndOrdering()
    {
        var root = (StackPanel)MarkdownRenderer.Build("1. first\n2. second\n\n- bullet\n", Anchor).Root;

        var markers = TextBlocks(root)
            .Select(TextOf)
            .Where(t => t is "1." or "2." or "•")
            .ToList();

        Assert.Equal(new[] { "1.", "2.", "•" }, markers);
    }

    [AvaloniaFact]
    public void TaskListItem_RendersACheckboxGlyph()
    {
        var root = (StackPanel)MarkdownRenderer.Build("- [x] done\n- [ ] pending\n", Anchor).Root;

        string all = string.Concat(TextBlocks(root).Select(TextOf));
        Assert.Contains("\u2611", all, StringComparison.Ordinal);
        Assert.Contains("\u2610", all, StringComparison.Ordinal);
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
    public void BlockQuote_RendersItsParagraphs()
    {
        var root = (StackPanel)MarkdownRenderer.Build("> quoted wisdom\n", Anchor).Root;

        Assert.Contains("quoted wisdom", TextBlocks(root).Select(TextOf), StringComparer.Ordinal);
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
    public void UnrecognizedFrontMatter_IsSkipped_WithoutCrashing()
    {
        var root = (StackPanel)MarkdownRenderer.Build("---\ntitle: something\n---\n\nbody text", Anchor).Root;

        string all = string.Concat(TextBlocks(root).Select(TextOf));
        Assert.Contains("body text", all, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void SoftWrapDamage_InTheSource_IsJustText()
    {
        // The grid reader hands the renderer already-joined logical lines; a very long paragraph
        // must flow through the text wrapper, not be re-broken by the renderer.
        var root = (StackPanel)MarkdownRenderer.Build(new string('x', 500), Anchor).Root;

        var paragraph = (SelectableTextBlock)root.Children[0];
        Assert.Equal(500, TextOf(paragraph).Length);
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
    public void Build_ReportsNoTransformBlock_ForOrdinaryMarkdown()
    {
        MarkdownRenderResult result = MarkdownRenderer.Build("# Title\n\nbody\n", Anchor);

        Assert.IsType<StackPanel>(result.Root);
        Assert.False(result.HasTransformBlock);
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
    public void DiffFence_ThroughBuild_ColorsLines_AndIsNotATransform()
    {
        MarkdownRenderResult result = MarkdownRenderer.Build(
            "```diff\n+added line\n-removed line\n```\n", Anchor);

        var body = Descendants((StackPanel)result.Root).OfType<SelectableTextBlock>().Single();
        var runs = body.Inlines!.OfType<Run>().ToList();

        Assert.Equal(2, runs.Count);
        Assert.NotEqual(runs[0].Foreground, runs[1].Foreground);

        // A diff-only response colors its lines but hides nothing, so it must offer no switch.
        Assert.False(result.HasTransformBlock);
    }

    [AvaloniaFact]
    public void MarkdownFence_SwitchOff_CopyYieldsRawSource()
    {
        string? copied = null;
        MarkdownRenderResult result = MarkdownRenderer.Build(
            "```markdown\n# Heading\n```\n",
            Anchor,
            onCopyText: text => copied = text,
            renderFencedMarkdown: false);

        Button copy = Descendants((StackPanel)result.Root).OfType<Button>().First(b => b.Content as string == "Copy");
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.NotNull(copied);
        Assert.Contains("#", copied!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void MarkdownFence_RawHtmlInside_StaysLiteral()
    {
        // Mirrors RawHtml_IsTreatedAsPlainText, one level deeper: the nested path runs a second
        // Markdig.Parse, and DisableHtml must still be in effect there.
        var root = (StackPanel)MarkdownRenderer.Build(
            "```markdown\nbefore\n\n<script>alert(1)</script>\n\nafter\n```\n",
            Anchor).Root;

        string all = string.Concat(TextBlocks(root).Select(TextOf));
        Assert.Contains("before", all, StringComparison.Ordinal);
        Assert.Contains("after", all, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void MarkdownFence_WhitespaceOnlyBody_RendersAsOrdinaryCodeBlock_WithNoSwitch()
    {
        MarkdownRenderResult result = MarkdownRenderer.Build("```markdown\n   \n```\n", Anchor);

        Assert.False(result.HasTransformBlock);
    }

    [AvaloniaFact]
    public void MarkdownFence_SwitchOffSourceBody_MatchesTheUnhandledBody()
    {
        const string body = "# Heading\n";

        var offRoot = (StackPanel)MarkdownRenderer.Build(
            $"```markdown\n{body}```\n", Anchor, renderFencedMarkdown: false).Root;
        var unrecognizedRoot = (StackPanel)MarkdownRenderer.Build(
            $"```notalang\n{body}```\n", Anchor).Root;

        var offBody = Descendants(offRoot).OfType<SelectableTextBlock>().Single();
        var unrecognizedBody = Descendants(unrecognizedRoot).OfType<SelectableTextBlock>().Single();

        Assert.Equal(unrecognizedBody.FontSize, offBody.FontSize);
        Assert.Equal(unrecognizedBody.FontFamily, offBody.FontFamily);
        Assert.Equal(unrecognizedBody.Foreground, offBody.Foreground);
        Assert.Equal(unrecognizedBody.TextWrapping, offBody.TextWrapping);
    }
}
