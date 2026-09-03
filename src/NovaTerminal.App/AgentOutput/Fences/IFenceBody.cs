using System;
using Avalonia.Controls;

namespace NovaTerminal.AgentOutput.Fences;

/// <summary>Renders a nested markdown document at the given depth.</summary>
internal delegate Control NestedMarkdownRenderer(string markdown, int depth);

/// <summary>What a handler is allowed to know about the render it sits inside.</summary>
internal sealed record FenceContext(
    int Depth,
    bool RenderFencedMarkdown,
    NestedMarkdownRenderer RenderNested,
    Action<string>? OnCopyText);

/// <summary>
/// Produces the body of one fenced code block, chosen by the fence's info string.
/// </summary>
/// <remarks>
/// A handler owns the <b>body only</b>. The border, header row, language label and Copy button
/// stay with the renderer, which keeps two properties that would otherwise erode one handler at
/// a time: every code block looks like every other one, and Copy always yields the raw source
/// no matter what is on screen.
/// </remarks>
internal interface IFenceBody
{
    /// <summary>
    /// True when this handler replaces the source with something else, and so participates in
    /// the panel's rendered/source switch. A restyle hides nothing and is not a transform.
    /// </summary>
    bool IsTransform { get; }

    Control Build(string code, MarkdownTheme theme, FenceContext context);
}
