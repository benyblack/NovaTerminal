using System;
using System.Collections.Generic;
using Avalonia.Input;
using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.Shell.Shortcuts;

namespace NovaTerminal.Controls
{
    /// <summary>
    /// Turns the catalogued Command Assist in-surface bindings into the two things the assist
    /// assembly consumes: the chords <c>CommandAssistKeyRouter</c> matches, and the key names the
    /// hint strip renders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>V2 Phase 3b, task 2.</strong> Lives on the App side of the Avalonia boundary because
    /// binding strings are <c>Avalonia.Input</c> chords and the catalogue is an App type. Nothing in
    /// <c>NovaTerminal.CommandAssist</c> parses a chord; it receives
    /// <see cref="AssistKeyBindings"/> and <see cref="AssistShortcutHintLabels"/> as plain data.
    /// </para>
    /// <para>
    /// <strong>The representability constraint, stated where it bites.</strong> Command Assist models
    /// five keys (<see cref="AssistKey"/>), because those are the only ones its router has ever needed
    /// to distinguish. An override naming anything else - <c>Ctrl+J</c>, <c>F5</c> - cannot be routed,
    /// and the wrong answer would be to pass it through as <see cref="AssistKey.None"/>: that is the
    /// value every unmapped key maps to, so the binding would either match every foreign key or (as
    /// <c>AssistKeyBinding.Matches</c> ensures) none, and in both cases the user's <c>Escape</c> would
    /// silently stop working. So an unrepresentable override falls back to the catalogue default and the
    /// key keeps working. The label follows the binding that is actually in force, so the hint strip
    /// stays truthful about it.
    /// </para>
    /// </remarks>
    internal static class AssistShortcutBindingResolver
    {
        internal const string DismissCommandId = "command_assist_dismiss";
        internal const string SelectionUpCommandId = "command_assist_selection_up";
        internal const string SelectionDownCommandId = "command_assist_selection_down";
        internal const string AcceptCommandId = "command_assist_accept";
        internal const string InsertCommandId = "command_assist_insert";

        /// <summary>The pin/unpin chord, dispatched from the window rather than by the router.</summary>
        internal const string PinCommandId = "command_assist_pin";

        internal static AssistShortcutBindings Resolve(IReadOnlyDictionary<string, string>? overrides)
        {
            AssistKeyBindings defaults = AssistKeyBindings.Default;

            ResolvedBinding dismiss = ResolveOne(overrides, DismissCommandId, defaults.Dismiss, "Esc");
            ResolvedBinding selectionUp = ResolveOne(overrides, SelectionUpCommandId, defaults.SelectionUp, "Up");
            ResolvedBinding selectionDown = ResolveOne(overrides, SelectionDownCommandId, defaults.SelectionDown, "Down");
            ResolvedBinding accept = ResolveOne(overrides, AcceptCommandId, defaults.Accept, "Enter");
            ResolvedBinding insert = ResolveOne(overrides, InsertCommandId, defaults.Insert, "Ctrl+Enter");

            return new AssistShortcutBindings(
                new AssistKeyBindings(
                    Dismiss: dismiss.Binding,
                    SelectionUp: selectionUp.Binding,
                    SelectionDown: selectionDown.Binding,
                    Accept: accept.Binding,
                    Insert: insert.Binding),
                new AssistShortcutHintLabels(
                    Accept: accept.Label,
                    SelectionUp: selectionUp.Label,
                    SelectionDown: selectionDown.Label,
                    Insert: insert.Label,
                    Dismiss: dismiss.Label));
        }

        private static ResolvedBinding ResolveOne(
            IReadOnlyDictionary<string, string>? overrides,
            string commandId,
            AssistKeyBinding defaultBinding,
            string defaultLabel)
        {
            if (overrides == null ||
                !overrides.TryGetValue(commandId, out string? binding) ||
                string.IsNullOrWhiteSpace(binding))
            {
                return new ResolvedBinding(defaultBinding, defaultLabel);
            }

            if (!ShortcutMatcher.TryParse(binding, out Key key, out KeyModifiers modifiers))
            {
                return new ResolvedBinding(defaultBinding, defaultLabel);
            }

            AssistKey assistKey = AssistKeyMapper.ToAssistKey(key);
            if (!IsRebindable(assistKey))
            {
                return new ResolvedBinding(defaultBinding, defaultLabel);
            }

            return new ResolvedBinding(
                new AssistKeyBinding(assistKey, AssistKeyMapper.ToAssistModifiers(modifiers)),
                BuildLabel(key, modifiers));
        }

        /// <summary>
        /// Whether an assist key may be the target of a rebind.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="AssistKey.None"/> is out because it is the value every key the assist does not model
        /// maps to, so a binding carrying it would either match every foreign key or none - see the class
        /// remarks.
        /// </para>
        /// <para>
        /// <strong><see cref="AssistKey.Tab"/> is out too (PR #293 review, non-blocking 3).</strong> It is
        /// modelled only so the router can be asked about it and answer "not mine": shell-first Tab is a
        /// documented promise - the keyboard table in <c>docs/command-assist/CommandAssist.md</c> says Tab
        /// is never taken by Command Assist, because at a shell prompt it is the shell's completion key and
        /// intercepting it is the single most disruptive thing this feature could do. Accepting it here
        /// would let a settings override quietly break that promise for <c>Escape</c>-shaped reasons:
        /// <c>AssistKeyBinding.Matches</c> would start claiming Tab, and the key the user's shell needs
        /// most would stop arriving. An override naming Tab falls back to the catalogue default, exactly
        /// like <c>Ctrl+J</c> or <c>F5</c>.
        /// </para>
        /// </remarks>
        private static bool IsRebindable(AssistKey key) =>
            key != AssistKey.None && key != AssistKey.Tab;

        /// <summary>
        /// Renders a chord the way the hint strip should read it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>One formatter, since the PR #293 review (non-blocking 5).</strong> The chord is
        /// rendered by <see cref="ShortcutMatcher.Format"/> - the same function the Settings shortcut
        /// editor uses to normalize a recorded key into a binding string - so a rebind reads identically
        /// in the hint strip and in the shortcut list. The hand-rolled version this replaced claimed to do
        /// that and did not: it fell back to <c>Key.ToString()</c>, so <c>Ctrl+1</c> showed as "Ctrl+D1"
        /// and <c>Ctrl+,</c> as "Ctrl+OemComma", neither of which is what Settings displays or what the
        /// user typed.
        /// </para>
        /// <para>
        /// Two keys are then overridden for display, and only for display. <c>Escape</c> is written "Esc"
        /// by every terminal UI in existence, and <c>Avalonia.Input.Key.Enter</c> is an alias for
        /// <c>Key.Return</c>, so the normalizer produces "Return" - what the key says on approximately no
        /// keyboard sold this century. The binding string keeps the canonical token; only the strip reads
        /// differently.
        /// </para>
        /// </remarks>
        private static string BuildLabel(Key key, KeyModifiers modifiers)
        {
            string normalized = ShortcutMatcher.Format(key, modifiers);
            return key switch
            {
                Key.Escape => ReplaceKeyToken(normalized, "Esc"),
                Key.Enter => ReplaceKeyToken(normalized, "Enter"),
                _ => normalized,
            };
        }

        /// <summary>Swaps the key token (always last) of a normalized chord for a display name.</summary>
        private static string ReplaceKeyToken(string normalized, string displayName)
        {
            int lastPlus = normalized.LastIndexOf('+');
            return lastPlus < 0 ? displayName : normalized[..(lastPlus + 1)] + displayName;
        }

        private readonly record struct ResolvedBinding(AssistKeyBinding Binding, string Label);
    }

    /// <summary>The resolved in-surface keyboard: what to match, and what to call it.</summary>
    internal sealed record AssistShortcutBindings(
        AssistKeyBindings Keys,
        AssistShortcutHintLabels HintLabels);
}
