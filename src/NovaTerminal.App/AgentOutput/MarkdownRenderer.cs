using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MInline = Markdig.Syntax.Inlines.Inline;

namespace NovaTerminal.AgentOutput;

/// <summary>
/// Renders markdown text as an Avalonia control tree for the Agent Output panel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parse and render are deliberately separate layers.</b> Markdig owns the parsing (its
/// advanced extensions bring GFM tables, task lists and strikethrough, and
/// <see cref="MarkdownPipelineBuilder.DisableHtml"/> keeps raw HTML out of the tree - agent
/// output is untrusted text, and an HTML passthrough would be an injection seam); this class owns
/// every pixel after that. There is no markdown XAML library in the dependency set, and the
/// panel's chrome has to match the pane's hand-styled surfaces, so a renderer of a few hundred
/// lines was the smaller dependency than a library's theming system.
/// </para>
/// <para>
/// <b>Colors resolve per build, not per binding.</b> <see cref="Build"/> prefers the app's
/// <c>Nt*</c> theme brushes (see <c>ThemePaletteResources</c>) and falls back to the same fixed
/// palette the sibling surfaces (RemoteFilesSidebar, the assist overlay) hard-code, so a theme
/// resource that is absent at runtime degrades to the known look instead of to transparent text
/// on transparent background. The renderer re-runs on every content change, so a theme switch is
/// picked up on the next rebuild.
/// </para>
/// <para>
/// <b>Unknown blocks are skipped, not crashed on.</b> The walk pattern-matches the block types
/// agent CLIs actually emit and ignores everything else; a future Markdig extension surfaces as
/// missing content, never as an exception mid-render.
/// </para>
/// </remarks>
public static class MarkdownRenderer
{
    private const string MonospaceFontFamily = "Cascadia Mono PL, Consolas, Menlo, monospace";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <param name="markdown">The raw markdown source.</param>
    /// <param name="resourceAnchor">
    /// Element whose resource scope the <c>Nt*</c> brushes are resolved from (usually the panel).
    /// </param>
    /// <param name="onCopyText">Invoked when the user copies a code block or inline run.</param>
    /// <param name="onOpenLink">Invoked when the user clicks a link.</param>
    public static Control Build(
        string markdown,
        StyledElement resourceAnchor,
        Action<string>? onCopyText = null,
        Action<string>? onOpenLink = null)
    {
        MarkdownDocument document = Markdown.Parse(markdown ?? string.Empty, Pipeline);
        var theme = MarkdownTheme.Resolve(resourceAnchor);

        var root = new StackPanel { Spacing = 2 };
        AppendBlocks(root.Children, document, theme, onCopyText, onOpenLink);
        return root;
    }

    private static void AppendBlocks(
        IList<Control> target,
        IEnumerable<Block> blocks,
        MarkdownTheme theme,
        Action<string>? onCopyText,
        Action<string>? onOpenLink)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    target.Add(BuildHeading(h, theme));
                    break;

                case ParagraphBlock paragraph:
                    target.Add(BuildParagraph(paragraph, theme, onCopyText, onOpenLink));
                    break;

                case FencedCodeBlock fenced:
                    target.Add(BuildCodeBlock(GetLinesText(fenced), fenced.Info.ToString(), theme, onCopyText));
                    break;

                case CodeBlock code:
                    target.Add(BuildCodeBlock(GetLinesText(code), null, theme, onCopyText));
                    break;

                case ListBlock list:
                    target.Add(BuildList(list, theme, onCopyText, onOpenLink));
                    break;

                case QuoteBlock quote:
                    target.Add(BuildQuote(quote, theme, onCopyText, onOpenLink));
                    break;

                case ThematicBreakBlock:
                    target.Add(new Border
                    {
                        Height = 1,
                        Margin = new Thickness(0, 8, 0, 8),
                        Background = theme.Hairline,
                    });
                    break;

                case Table table:
                    target.Add(BuildTable(table, theme, onCopyText, onOpenLink));
                    break;


                    // YamlFrontMatter and anything else unrecognized: skip.
            }
        }
    }

    private static Control BuildHeading(HeadingBlock heading, MarkdownTheme theme)
    {
        double size = heading.Level switch
        {
            1 => 21,
            2 => 18,
            3 => 15.5,
            4 => 14,
            _ => 13,
        };

        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = FontWeight.SemiBold,
            Foreground = theme.Foreground,
            Margin = new Thickness(0, heading.Level <= 2 ? 10 : 8, 0, 4),
        };
        AppendInlines(textBlock.Inlines, heading.Inline, theme, onCopyText: null, onOpenLink: null);
        return textBlock;
    }

    private static Control BuildParagraph(
        ParagraphBlock paragraph,
        MarkdownTheme theme,
        Action<string>? onCopyText,
        Action<string>? onOpenLink)
    {
        var textBlock = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = theme.Foreground,
            Margin = new Thickness(0, 3, 0, 3),
        };
        AppendInlines(textBlock.Inlines, paragraph.Inline, theme, onCopyText, onOpenLink);
        return textBlock;
    }

    private static Control BuildCodeBlock(
        string code,
        string? language,
        MarkdownTheme theme,
        Action<string>? onCopyText)
    {
        var codeText = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontFamily = new FontFamily(MonospaceFontFamily),
            Foreground = theme.Foreground,
        };
        codeText.Inlines?.Add(new Run { Text = code });

        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        if (!string.IsNullOrWhiteSpace(language))
        {
            header.Children.Add(new TextBlock
            {
                Text = language,
                FontSize = 11,
                Foreground = theme.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var copyButton = new Button
        {
            Content = "Copy",
            FontSize = 10.5,
            Padding = new Thickness(6, 1),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        copyButton.Click += (_, _) => onCopyText?.Invoke(code);
        TrySetHandCursor(copyButton);
        Grid.SetColumn(copyButton, 1);
        header.Children.Add(copyButton);

        return new Border
        {
            Background = theme.CodeBackground,
            BorderBrush = theme.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 6, 0, 6),
            Child = new StackPanel
            {
                Children =
                {
                    new Border
                    {
                        BorderBrush = theme.Hairline,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(8, 4, 8, 3),
                        Child = header,
                    },
                    new Border
                    {
                        Padding = new Thickness(8, 6, 8, 8),
                        Child = codeText,
                    },
                },
            },
        };
    }

    private static Control BuildList(
        ListBlock list,
        MarkdownTheme theme,
        Action<string>? onCopyText,
        Action<string>? onOpenLink)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
        int itemNumber = ParseOrderedStart(list);

        foreach (Block item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            string bullet = list.IsOrdered ? $"{itemNumber++}." : "•";
            bool? checkedState = TryGetTaskState(listItem);

            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                Margin = new Thickness(0, 1),
            };

            var markerText = new TextBlock
            {
                Text = ResolveItemMarker(bullet, checkedState),
                FontSize = 13,
                Foreground = checkedState.HasValue ? theme.Accent : theme.Secondary,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12, 0, 6, 0),
            };
            row.Children.Add(markerText);

            var itemContent = new StackPanel { Spacing = 2 };
            AppendBlocks(itemContent.Children, listItem, theme, onCopyText, onOpenLink);
            Grid.SetColumn(itemContent, 1);
            row.Children.Add(itemContent);

            panel.Children.Add(row);
        }

        return panel;
    }

    private static Control BuildQuote(
        QuoteBlock quote,
        MarkdownTheme theme,
        Action<string>? onCopyText,
        Action<string>? onOpenLink)
    {
        var content = new StackPanel { Spacing = 2 };
        AppendBlocks(content.Children, quote, theme, onCopyText, onOpenLink);

        return new Border
        {
            BorderBrush = theme.Hairline,
            BorderThickness = new Thickness(0, 0, 0, 0),
            Background = theme.PanelBackground,
            CornerRadius = new CornerRadius(0, 4, 4, 0),
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(10, 4, 8, 4),
            Child = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                Children =
                {
                    new Border
                    {
                        Width = 2,
                        Margin = new Thickness(0, 2, 8, 2),
                        Background = theme.Accent,
                    },
                    content,
                },
            },
        };
    }

    private static Control BuildTable(
        Table table,
        MarkdownTheme theme,
        Action<string>? onCopyText,
        Action<string>? onOpenLink)
    {
        int columnCount = Math.Max(table.ColumnDefinitions.Count, 1);
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        for (int i = 0; i < columnCount; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int rowIndex = 0;
        foreach (Block rowBlock in table)
        {
            if (rowBlock is not TableRow row)
            {
                continue;
            }

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int columnIndex = 0;

            foreach (Block cellBlock in row)
            {
                if (cellBlock is not TableCell cell)
                {
                    continue;
                }

                var cellContent = new StackPanel { Spacing = 2 };
                AppendBlocks(cellContent.Children, cell, theme, onCopyText, onOpenLink);

                if (row.IsHeader)
                {
                    Boldify(cellContent);
                }

                var cellBorder = new Border
                {
                    BorderBrush = theme.Hairline,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(7, 4),
                    Background = row.IsHeader ? theme.PanelBackground : null,
                    Child = cellContent,
                };

                Grid.SetRow(cellBorder, rowIndex);
                Grid.SetColumn(cellBorder, Math.Min(columnIndex, columnCount - 1));
                grid.Children.Add(cellBorder);
                columnIndex++;
            }

            rowIndex++;
        }

        // Close the left and top edges of the cell borders drawn above.
        return new Border
        {
            BorderBrush = theme.Hairline,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Child = grid,
        };
    }

    private static void AppendInlines(
        InlineCollection target,
        ContainerInline? container,
        MarkdownTheme theme,
        Action<string>? onCopyText,
        Action<string>? onOpenLink)
    {
        if (container is null)
        {
            return;
        }

        foreach (MInline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run { Text = literal.Content.ToString() });
                    break;

                case CodeInline code:
                    target.Add(new InlineUIContainer
                    {
                        Child = new Border
                        {
                            Background = theme.CodeBackground,
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(4, 0.5, 4, 1),
                            Child = new TextBlock
                            {
                                Text = code.Content,
                                FontSize = 12,
                                FontFamily = new FontFamily(MonospaceFontFamily),
                                Foreground = theme.Secondary,
                            },
                        },
                    });
                    break;

                case EmphasisInline emphasis:
                    AppendEmphasis(target, emphasis, theme, onCopyText, onOpenLink);
                    break;

                case LinkInline link when link.IsImage:
                    // No image support in v1: keep the link text so nothing disappears silently.
                    AppendLink(target, link, theme, onOpenLink);
                    break;

                case LinkInline link:
                    AppendLink(target, link, theme, onOpenLink);
                    break;

                case AutolinkInline autolink:
                    AppendLinkTarget(
                        target,
                        autolink.Url,
                        autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url,
                        theme,
                        onOpenLink);
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case TaskList taskList:
                    target.Add(new Run
                    {
                        Text = taskList.Checked ? "☑ " : "☐ ",
                        Foreground = theme.Accent,
                    });
                    break;

                case ContainerInline nested:
                    AppendInlines(target, nested, theme, onCopyText, onOpenLink);
                    break;

                    // HtmlInline / HtmlEntityInline: deliberately dropped (DisableHtml already kept
                    // raw HTML blocks out; these are stragglers).
            }
        }
    }

    private static void AppendEmphasis(
        InlineCollection target,
        EmphasisInline emphasis,
        MarkdownTheme theme,
        Action<string>? onCopyText,
        Action<string>? onOpenLink)
    {
        bool isStrike = emphasis.DelimiterChar == '~';
        bool isBold = !isStrike && emphasis.DelimiterCount >= 2;

        var outer = new Span();
        AppendInlines(outer.Inlines, emphasis, theme, onCopyText, onOpenLink);

        if (isStrike)
        {
            outer.TextDecorations = TextDecorations.Strikethrough;
        }
        else if (isBold)
        {
            outer.FontWeight = FontWeight.Bold;
        }
        else
        {
            outer.FontStyle = FontStyle.Italic;
        }

        target.Add(outer);
    }

    private static void AppendLink(
        InlineCollection target,
        LinkInline link,
        MarkdownTheme theme,
        Action<string>? onOpenLink)
    {
        AppendLinkTarget(target, link.Url, GetLiteralText(link), theme, onOpenLink);
    }

    private static void AppendLinkTarget(
        InlineCollection target,
        string url,
        string label,
        MarkdownTheme theme,
        Action<string>? onOpenLink)
    {
        var linkText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(label) ? url : label,
            FontSize = 13,
            Foreground = theme.Accent,
            TextDecorations = TextDecorations.Underline,
        };
        linkText.PointerPressed += (_, _) => onOpenLink?.Invoke(url);
        TrySetHandCursor(linkText);
        target.Add(new InlineUIContainer { Child = linkText });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Hand cursor for click affordances. Cursor instantiation needs the platform's cursor
    /// factory, which only exists once an Avalonia application is up - plain unit tests construct
    /// these controls without one, so the affordance silently degrades to a normal cursor there.
    /// </summary>
    private static void TrySetHandCursor(InputElement element)
    {
        if (Application.Current is not null)
        {
            element.Cursor = new Cursor(StandardCursorType.Hand);
        }
    }

    /// <summary>The code block's source, joined from its physical lines.</summary>
    private static string GetLinesText(CodeBlock codeBlock)
    {
        var builder = new StringBuilder();
        foreach (StringLine line in codeBlock.Lines)
        {
            builder.AppendLine(line.ToString());
        }

        return builder.ToString().TrimEnd((char)13, (char)10);
    }

    private static int ParseOrderedStart(ListBlock list)
    {
        if (!list.IsOrdered || !int.TryParse(list.OrderedStart.ToString(), out int start))
        {
            return 1;
        }

        return start;
    }

    /// <summary>
    /// Task-list state, when the item is one: Markdig 1.x parses the checkbox marker into a
    /// TaskList inline at the head of the item's first paragraph.
    /// </summary>
    private static bool? TryGetTaskState(ListItemBlock listItem)
    {
        foreach (Block block in listItem)
        {
            if (block is ParagraphBlock { Inline: not null } paragraph)
            {
                foreach (MInline inline in paragraph.Inline)
                {
                    if (inline is TaskList taskList)
                    {
                        return taskList.Checked;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The glyph a list item starts with: the checkbox for task items, else the bullet.</summary>
    private static string ResolveItemMarker(string bullet, bool? checkedState)
    {
        if (!checkedState.HasValue)
        {
            return bullet;
        }

        return checkedState.Value ? "☑" : "☐";
    }

    private static void Boldify(StackPanel panel)
    {
        foreach (Control child in panel.Children)
        {
            if (child is TextBlock textBlock)
            {
                textBlock.FontWeight = FontWeight.SemiBold;
            }
        }
    }

    /// <summary>The plain text of an inline subtree (links, emphasis), literals only.</summary>
    private static string GetLiteralText(ContainerInline container)
    {
        var builder = new StringBuilder();
        CollectLiteralText(container, builder);
        return builder.ToString();
    }

    private static void CollectLiteralText(ContainerInline container, StringBuilder builder)
    {
        foreach (MInline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.ToString());
                    break;

                case CodeInline code:
                    builder.Append(code.Content);
                    break;

                case ContainerInline nested:
                    CollectLiteralText(nested, builder);
                    break;
            }
        }
    }

}
