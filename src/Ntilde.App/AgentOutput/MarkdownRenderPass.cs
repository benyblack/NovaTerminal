namespace NovaTerminal.AgentOutput;

/// <summary>Mutable state for one render pass of <see cref="MarkdownRenderer.Build"/>.</summary>
/// <remarks>
/// Two jobs, both of which have to cross the recursive block walk. <see cref="RenderFencedMarkdown"/>
/// travels down: it is the panel's switch, and a fence handler that transforms content has to
/// honour it. <see cref="HasTransformBlock"/> travels up: the panel shows its switch only when
/// the render actually produced something the switch governs, so a response with no such block
/// carries no pointless control.
/// </remarks>
internal sealed class MarkdownRenderPass
{
    internal required bool RenderFencedMarkdown { get; init; }

    /// <summary>Set by any handler whose <c>IsTransform</c> is true, at any depth.</summary>
    internal bool HasTransformBlock { get; set; }
}
