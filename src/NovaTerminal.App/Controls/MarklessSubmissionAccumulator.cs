using System.Globalization;
using System.Text;
using Avalonia.Input;

namespace NovaTerminal.Controls
{
    /// <summary>
    /// What the user typed on the current command line, for the sessions where nobody can read it
    /// off the grid: `cmd.exe`, any shell whose integration bootstrap bailed out, and every SSH
    /// host the user has not instrumented. Used <em>only</em> as the Enter-time submission text for
    /// history capture, never as the Command Assist query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not V1's shadow buffer.</b> The mirror that Phase 1c deleted answered "what is on
    /// the command line?" with a guess, and its failure mode was silent wrongness: `Ctrl+U` left it
    /// holding text that was no longer on screen, Tab completions were truncated to what the user
    /// had typed, and all of it was written to permanent history as commands the user had run.
    /// </para>
    /// <para>
    /// This one is <b>poisoned</b> by every edit it cannot model. It knows exactly two operations —
    /// append the characters the user typed, and remove the last one on Backspace — and anything
    /// else at all (an arrow key, `Home`, `Delete`, `Tab`, a page key, an F-key, any unowned
    /// `Ctrl`/`Alt` chord, a paste, an assist insertion) turns it off until the line is reset. Its
    /// only two outcomes are therefore "exactly the characters the user typed, in order" and
    /// "nothing", which is the same bar the grid reader is held to, reached by a different
    /// mechanism. Recording nothing is recoverable; recording a command the user never ran is not.
    /// </para>
    /// <para>
    /// <b>It is not the last gate either.</b> Knowing what the user typed is not the same as knowing
    /// the shell was listening: at a no-echo prompt (`ssh` asking for a password) every keystroke
    /// still arrives here. <c>TerminalPane.ReadEchoedMarklessSubmission</c> therefore requires this
    /// class's answer to be painted on the grid at the cursor before it is used.
    /// </para>
    /// <para>
    /// Deletable in one commit once every session has real `OSC 133` marks.
    /// </para>
    /// </remarks>
    internal sealed class MarklessSubmissionAccumulator
    {
        /// <summary>
        /// Beyond this many characters the line stops being a command anyone typed by hand, and
        /// the accumulator stops paying to hold it. Poisoning rather than truncating, because a
        /// truncated command line is exactly the "something false" this class exists to avoid.
        /// </summary>
        internal const int MaxTrackedLength = 8192;

        private readonly StringBuilder _buffer = new();

        /// <remarks>
        /// <c>volatile</c> because <see cref="Poison"/> is reachable off the UI thread:
        /// <c>AgentSessionRegistration.InputInjected</c> fires on whatever thread the agent-host
        /// IPC endpoint is serving, and <c>TerminalPane.NotifyExternalInputSent</c> calls straight
        /// through. Every other member runs on the UI thread, so this one write is the whole of the
        /// cross-thread surface — and a poison that the UI thread never observes is a command the
        /// user never typed written to permanent history. Same reasoning, and the same fix, as the
        /// pane's <c>_hasUnechoedInput</c>.
        /// </remarks>
        private volatile bool _poisoned;

        internal bool IsPoisoned => _poisoned;

        /// <summary>
        /// The accumulator can describe the current line <em>and</em> that description is "empty".
        /// </summary>
        /// <remarks>
        /// The one thing this class knows that is useful outside history capture. In a markless session
        /// there is no grid snapshot, so V2 Phase 3a asks this before allowing a suggestion to be
        /// inserted: unpoisoned means nothing the accumulator cannot model has happened since the line
        /// was reset, and empty means nothing has been typed - between them, the line is empty and a
        /// whole command may be sent to it. See
        /// <c>TerminalPane.TryReadInsertionQuerySnapshot</c> for the rest of the gate (the echo flag,
        /// paste suppression, the alt screen).
        /// </remarks>
        internal bool IsCleanAndEmpty => !_poisoned && _buffer.Length == 0;

        /// <summary>The user typed printable text and the pane sent it to the PTY.</summary>
        internal void AppendTypedText(string? text)
        {
            if (_poisoned || string.IsNullOrEmpty(text))
            {
                return;
            }

            // A newline arriving as text input rather than as an Enter key press is not a keystroke
            // this class models: it submits (possibly several) lines without going through the
            // Enter path that reads and resets. CapturePipeline rejects multi-line submissions too,
            // but the accumulator must not carry stale text into the next line either.
            if (text.Contains('\n') || text.Contains('\r') || _buffer.Length + text.Length > MaxTrackedLength)
            {
                Poison();
                return;
            }

            _buffer.Append(text);
        }

        /// <summary>
        /// Backspace: the shell's line editor removed the last character, so remove ours.
        /// </summary>
        /// <remarks>
        /// On an empty buffer this is a no-op rather than a poison: the buffer is empty exactly
        /// when the line is, and backspace at an empty prompt does nothing in every shell.
        /// It <em>does</em> poison when "the last character" is ambiguous — a surrogate pair or a
        /// combining sequence, where what the shell deletes and what one UTF-16 unit is are not
        /// reliably the same thing.
        /// </remarks>
        internal void ObserveBackspace()
        {
            if (_poisoned)
            {
                return;
            }

            if (_buffer.Length == 0)
            {
                return;
            }

            char last = _buffer[_buffer.Length - 1];
            if (char.IsSurrogate(last) || IsCombining(last))
            {
                Poison();
                return;
            }

            _buffer.Length--;
        }

        /// <summary>Something happened to the command line that this class cannot model.</summary>
        internal void Poison() => _poisoned = true;

        /// <summary>New command line: forget the text and clear the poison.</summary>
        internal void Reset()
        {
            _buffer.Clear();
            _poisoned = false;
        }

        /// <summary>
        /// What the user submitted, or <see langword="null"/> when the accumulator stopped being
        /// able to say. Does not reset — the caller resets after the read, because Enter is both
        /// the read point and the reset point and the order matters.
        /// </summary>
        internal string? TryReadSubmission() => _poisoned ? null : _buffer.ToString();

        /// <summary>
        /// How a key press observed at <c>TerminalPane.TryHandleCommandAssistKey</c> affects the
        /// accumulator.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="modifiers">Its modifiers.</param>
        /// <param name="wasHandledByAssist">
        /// Whether Command Assist consumed the key. When it did, the key never reached
        /// <c>TerminalView</c>'s input encoder and so never reached the shell, and the command line
        /// is untouched. (`Ctrl+Enter` is the exception, because accepting a suggestion *sends*
        /// text; the pane poisons at that call site rather than here.)
        /// </param>
        /// <remarks>
        /// The classification is an <b>allow-list</b>, and that direction is deliberate: a key this
        /// method has never heard of poisons. An allow-list that is missing a harmless key costs a
        /// capture; a deny-list that is missing a line-editing key writes a wrong command to
        /// history, and those two mistakes are not the same size.
        /// </remarks>
        internal static AccumulatorKeyEffect ClassifyKey(Key key, KeyModifiers modifiers, bool wasHandledByAssist)
        {
            if (wasHandledByAssist)
            {
                return AccumulatorKeyEffect.None;
            }

            bool isCtrl = (modifiers & KeyModifiers.Control) != 0;
            bool isAlt = (modifiers & KeyModifiers.Alt) != 0;
            bool isMeta = (modifiers & KeyModifiers.Meta) != 0;

            if (IsModifierOnly(key))
            {
                return AccumulatorKeyEffect.None;
            }

            // Ctrl+C abandons the line. TerminalView routes it to copy-to-clipboard when there is a
            // selection, which leaves the line intact, but resetting on a copy loses a capture and
            // resetting on the interrupt is required: the cheap uniform rule errs the safe way.
            if (isCtrl && !isAlt && key == Key.C)
            {
                return AccumulatorKeyEffect.Reset;
            }

            // Enter submits and Backspace chops, and both are modeled — but only completely
            // unmodified. Any modifier at all poisons, for a reason that is not obvious from the
            // legacy encoder: with the kitty keyboard protocol's disambiguate tier active,
            // TerminalView encodes a *modified* Enter or Backspace as a CSI u sequence
            // (`Ctrl+Backspace` -> `CSI 127;5u`, `Shift+Enter` -> `CSI 13;2u`) and takes the early
            // return, so `EnterObserved` / `BackspaceObserved` never fire. The accumulator would
            // then keep every character while a kitty-aware line editor deleted a whole word, or
            // keep collecting on a line the shell has already broken. The modifier is not visible
            // in the events this class is fed, so the classifier has to refuse it up front.
            //
            // Cost: one lost capture per `Ctrl+Backspace` or `Shift+Enter`. That is the fail-closed
            // direction, and gating on "is kitty active right now" instead would make the
            // classification depend on mutable terminal state that can change mid-line.
            if ((key == Key.Enter || key == Key.Back) && modifiers == KeyModifiers.None)
            {
                return AccumulatorKeyEffect.None;
            }

            // AltGr. On Windows, Avalonia reports it as Control|Alt — there is no separate
            // KeyModifiers.AltGr — so without this carve-out every accented or symbol character
            // that needs AltGr on a German, French, Nordic, Turkish or Polish layout would poison
            // the line it appears in. That is `@`, `{`, `[`, `\`, `|`, `~` and more: on those
            // layouts the accumulator would capture essentially nothing.
            //
            // This is fail-closed, not a hole, and the proof is in TerminalView: for Ctrl+Alt plus
            // a text-producing key it sends *nothing* to the PTY. `EncodeKittyKey` returns null on
            // the Control|Alt pair (TerminalInputModeEncoder.cs), `EncodeAltKey` returns null for
            // the same pair, and the legacy Ctrl branch requires `!Alt`. So the only way those
            // bytes reach the shell is the composed WM_CHAR that arrives as `OnTextInput` — which
            // is exactly the event this class observes and appends. The keypress changes the line
            // only through a path the accumulator can see.
            //
            // Non-text-producing Ctrl+Alt chords (Enter, Backspace, Tab, Escape, arrows) fall
            // through to the poison below, because those *do* reach the shell as control bytes.
            if (isCtrl && isAlt && !isMeta && IsTextProducing(key))
            {
                return AccumulatorKeyEffect.None;
            }

            // Printable keys with no chord modifier become an Avalonia TextInput event, and the
            // append happens there with the actual text. Meta is included as a chord modifier:
            // Cmd+key on macOS is an application shortcut, not a character.
            if (!isCtrl && !isAlt && !isMeta && IsTextProducing(key))
            {
                return AccumulatorKeyEffect.None;
            }

            return AccumulatorKeyEffect.Poison;
        }

        internal void ApplyKey(Key key, KeyModifiers modifiers, bool wasHandledByAssist)
        {
            switch (ClassifyKey(key, modifiers, wasHandledByAssist))
            {
                case AccumulatorKeyEffect.Poison:
                    Poison();
                    break;
                case AccumulatorKeyEffect.Reset:
                    Reset();
                    break;
            }
        }

        private static bool IsModifierOnly(Key key) => key switch
        {
            Key.None or
            Key.LeftShift or Key.RightShift or
            Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LWin or Key.RWin => true,
            _ => false,
        };

        /// <summary>
        /// Keys that produce a character rather than a control sequence. Everything that is not on
        /// this list poisons, so it is written to be over- rather than under-inclusive of the
        /// harmless: `Key.ImeProcessed` and `Key.DeadCharProcessed` are here because composition
        /// ends in a TextInput event carrying the composed character (or in nothing at all, which
        /// leaves the buffer correct either way).
        /// </summary>
        private static bool IsTextProducing(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return true;
            }

            if (key >= Key.D0 && key <= Key.D9)
            {
                return true;
            }

            if (key >= Key.NumPad0 && key <= Key.Divide)
            {
                // NumPad0..NumPad9, Multiply, Add, Separator, Subtract, Decimal, Divide.
                return true;
            }

            return key switch
            {
                Key.Space or
                Key.OemPlus or Key.OemComma or Key.OemMinus or Key.OemPeriod or
                Key.Oem1 or Key.Oem2 or Key.Oem3 or Key.Oem4 or
                Key.Oem5 or Key.Oem6 or Key.Oem7 or Key.Oem8 or
                Key.Oem102 or
                Key.AbntC1 or Key.AbntC2 or
                Key.ImeProcessed or Key.DeadCharProcessed => true,
                _ => false,
            };
        }

        private static bool IsCombining(char value) =>
            CharUnicodeInfo.GetUnicodeCategory(value) is
                UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark;
    }

    /// <summary>What a key press does to <see cref="MarklessSubmissionAccumulator"/>.</summary>
    internal enum AccumulatorKeyEffect
    {
        /// <summary>The key is modeled elsewhere, or never reached the shell. Leave state alone.</summary>
        None,

        /// <summary>The command line changed in a way the accumulator cannot follow.</summary>
        Poison,

        /// <summary>The line was abandoned; start a fresh one.</summary>
        Reset,
    }
}
