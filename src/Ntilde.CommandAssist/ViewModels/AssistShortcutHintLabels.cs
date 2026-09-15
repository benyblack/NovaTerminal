namespace NovaTerminal.CommandAssist.ViewModels;

/// <summary>
/// The key names the assist hint strip renders, so the strip can advertise rebound shortcuts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>V2 Phase 3b, task 2.</strong> Phase 3a bound <c>ShortcutHintText</c> and made it
/// state-dependent, but the key names in it were string literals - so a user who rebound accept off
/// <c>Enter</c> was told to press <c>Enter</c>. The keys are in the App's shortcut catalogue now, and
/// the catalogue speaks <c>Avalonia.Input</c> chords that this assembly is forbidden to reference
/// (<c>CommandAssist_must_not_depend_on_Avalonia</c>). So the host resolves the bindings to display
/// strings and pushes them in; nothing here parses a chord.
/// </para>
/// <para>
/// The defaults reproduce the shipped Phase 3a strings exactly. That matters for more than tests: a
/// controller with no host attached - the MCP surface, a unit test, the XAML designer - has no
/// catalogue to read and must still describe the default keyboard truthfully.
/// </para>
/// </remarks>
/// <param name="Accept">
/// The key that inserts the selected row while browsing. Default <c>Enter</c>.
/// </param>
/// <param name="SelectionUp">The key that moves the selection up. Default <c>Up</c>.</param>
/// <param name="SelectionDown">The key that moves the selection down. Default <c>Down</c>.</param>
/// <param name="Insert">
/// The key that inserts in any state, browsing or not. Default <c>Ctrl+Enter</c>.
/// </param>
/// <param name="Dismiss">The key that closes the surface. Default <c>Esc</c>.</param>
public sealed record AssistShortcutHintLabels(
    string Accept = "Enter",
    string SelectionUp = "Up",
    string SelectionDown = "Down",
    string Insert = "Ctrl+Enter",
    string Dismiss = "Esc")
{
    /// <summary>The shipped default keyboard, described in the shipped default words.</summary>
    public static AssistShortcutHintLabels Default { get; } = new();
}
