using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace NovaTerminal.AgentOutput.Fences;

/// <summary>
/// Renders a <c>markdown</c> / <c>md</c> fence as a nested document rather than as source.
/// </summary>
/// <remarks>
/// <para>
/// Recursion goes back through the renderer's own block walk rather than reimplementing it, so
/// every block type the panel already supports - headings, tables, task lists, links with their
/// scheme allowlist - works inside a fence with no duplicated rendering logic.
/// </para>
/// <para>
/// The switch is honoured here rather than at the resolver, because a handler that renders
/// source must still report itself as a transform: that is what keeps the panel's switch on
/// screen, and a switch that hid itself when flipped would be a one-way door.
/// </para>
/// </remarks>
internal sealed class MarkdownFenceBody : IFenceBody
{
    private const string MonospaceFontFamily = "Cascadia Mono PL, Consolas, Menlo, monospace";

    public bool IsTransform => true;

    public Control Build(string code, MarkdownTheme theme, FenceContext context)
    {
        if (!context.RenderFencedMarkdown)
        {
            return BuildSource(code, theme);
        }

        return context.RenderNested(code, context.Depth + 1);
    }

    /// <summary>The unhandled path's body, reproduced for the switch's source position.</summary>
    private static Control BuildSource(string code, MarkdownTheme theme)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontFamily = new FontFamily(MonospaceFontFamily),
            Foreground = theme.Foreground,
        };
        text.Inlines?.Add(new Run { Text = code });
        return text;
    }
}
