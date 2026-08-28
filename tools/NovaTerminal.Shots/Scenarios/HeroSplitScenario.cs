using System.Reflection;
using Avalonia.Layout;
using NovaTerminal.Controls;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The lead hero shot: one tab split into three live panes. This is the image the README leads
/// with, so every pane must be genuinely full of its own content rather than an empty split that
/// happened to catch a pane before its shell answered.
/// </summary>
internal sealed class HeroSplitScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "hero-split",
        Tier: 1,
        LogicalWidth: 1440,
        LogicalHeight: 900,
        Intent: "Three panes at once: a colourful test run on the left, a git graph top-right, and " +
                "a process monitor bottom-right. Every pane full of text, splitters clearly visible.");

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane left = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(left, "clear");
        await context.RunCommandAsync(left, "bash scripts/demo-test.sh");

        context.Driver.InvokePrivate(context.Window, "SplitPane", Orientation.Horizontal);
        TerminalPane topRight = CurrentPane(context);
        await context.RunCommandAsync(topRight, "git log --graph --oneline --all -12");

        context.Driver.InvokePrivate(context.Window, "SplitPane", Orientation.Vertical);
        TerminalPane bottomRight = CurrentPane(context);
        await context.RunCommandAsync(bottomRight, "ps aux | head -20");

        context.Capture();
    }

    /// <summary>
    /// Reads MainWindow's notion of "the pane the next split/command targets".
    /// </summary>
    /// <remarks>
    /// The task brief describes <c>_currentPane</c> as a private field and reaches it with
    /// <c>GetField</c>. That is wrong: MainWindow.axaml.cs:49-51 declares it as a private
    /// <b>property</b> (backed by the field <c>_currentPaneValue</c> at :48), and its setter has
    /// a side effect — flipping <c>IsActivePane</c> on the old and new pane — that a raw field
    /// read would still observe correctly, so either route would have been safe to read from.
    /// This uses <c>GetProperty</c> because that is what the member actually is; reading the
    /// backing field directly would keep working today but silently stop matching the source the
    /// moment the property gained real get-time logic instead of a plain pass-through.
    /// </remarks>
    private static TerminalPane CurrentPane(ShotContext context)
    {
        PropertyInfo property = typeof(MainWindow).GetProperty(
            "_currentPane", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow._currentPane no longer exists.");

        return (TerminalPane)property.GetValue(context.Window)!;
    }
}
