using Avalonia.Controls;

namespace NovaTerminal.AgentOutput.Fences;

/// <summary>Renders a markdown fence as a nested document. Filled in by Task 4.</summary>
internal sealed class MarkdownFenceBody : IFenceBody
{
    public bool IsTransform => true;

    public Control Build(string code, MarkdownTheme theme, FenceContext context)
        => context.RenderNested(code, context.Depth + 1);
}
