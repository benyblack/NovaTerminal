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
            if (assistKey == AssistKey.None)
            {
                return new ResolvedBinding(defaultBinding, defaultLabel);
            }

            return new ResolvedBinding(
                new AssistKeyBinding(assistKey, AssistKeyMapper.ToAssistModifiers(modifiers)),
                BuildLabel(key, modifiers));
        }

        /// <summary>
        /// Renders a chord the way the hint strip should read it.
        /// </summary>
        /// <remarks>
        /// Not the key's own name verbatim, for two keys. <c>Escape</c> is written "Esc" by every
        /// terminal UI in existence, and <c>Avalonia.Input.Key.Enter</c> is an alias for
        /// <c>Key.Return</c>, so <c>ToString()</c> on it produces "Return" - which is what the key says
        /// on approximately no keyboard sold this century. Everything else is the key's name, so a
        /// rebind reads the same way here as it does in the Settings shortcut list.
        /// </remarks>
        private static string BuildLabel(Key key, KeyModifiers modifiers)
        {
            List<string> parts = new(4);
            if ((modifiers & KeyModifiers.Control) != 0)
            {
                parts.Add("Ctrl");
            }

            if ((modifiers & KeyModifiers.Alt) != 0)
            {
                parts.Add("Alt");
            }

            if ((modifiers & KeyModifiers.Shift) != 0)
            {
                parts.Add("Shift");
            }

            parts.Add(key switch
            {
                Key.Escape => "Esc",
                Key.Enter => "Enter",
                _ => key.ToString(),
            });
            return string.Join("+", parts);
        }

        private readonly record struct ResolvedBinding(AssistKeyBinding Binding, string Label);
    }

    /// <summary>The resolved in-surface keyboard: what to match, and what to call it.</summary>
    internal sealed record AssistShortcutBindings(
        AssistKeyBindings Keys,
        AssistShortcutHintLabels HintLabels);
}
