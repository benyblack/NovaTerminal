namespace NovaTerminal.VT
{
    /// <summary>
    /// Encapsulates terminal mode flags (mouse reporting, auto-wrap, cursor keys, etc.)
    /// </summary>
    public class ModeState
    {
        // Mouse reporting modes (for TUI apps like vim, htop)
        public bool MouseModeX10 { get; set; }          // ?1000 - X10 mouse reporting
        public bool MouseModeButtonEvent { get; set; }  // ?1002 - Button event tracking
        public bool MouseModeAnyEvent { get; set; }     // ?1003 - Any event tracking
        public bool MouseModeSGR { get; set; }          // ?1006 - SGR extended mode

        // Cursor and display modes
        public bool IsApplicationCursorKeys { get; set; } // ?1 - DECCKM (Application Cursor Keys)
        public bool IsAutoWrapMode { get; set; } = true;  // ?7 - DECAWM (Auto Wrap Mode)
        public bool IsOriginMode { get; set; }            // ?6 - DECOM (Origin Mode)
        public bool IsFocusEventReporting { get; set; }   // ?1004 - FocusIn/FocusOut reporting
        public bool IsBracketedPasteMode { get; set; }    // ?2004 - Bracketed Paste Mode
        public bool IsCursorVisible { get; set; } = true; // ?25 - DECTCEM (Text Cursor Enable Mode)
        public bool IsCursorBlinkEnabled { get; set; } = true;
        public CursorStyle CursorStyle { get; set; } = CursorStyle.Underline;
        public bool IsInsertMode { get; set; }            //  4 - IRM (Insert Replacement Mode)
        public bool IsLineFeedNewLineMode { get; set; }   // 20 - LNM (Line Feed New Line Mode)
        public bool IsEchoEnabled { get; set; } = true;   // 12 - SRM (Send/Receive Mode)

        /// <summary>
        /// Kitty keyboard protocol progressive-enhancement flags (CSI ? u / CSI &gt; u /
        /// CSI &lt; u / CSI = u). Per-screen-buffer flag stacks; the App input layer reads
        /// <see cref="KittyKeyboardState.DisambiguateEscapeCodes"/> when encoding key events.
        /// </summary>
        public KittyKeyboardState KittyKeyboard { get; set; } = new KittyKeyboardState();

        /// <summary>
        /// Clears the input-reporting modes a full-screen application turns on for itself
        /// (mouse tracking, focus reporting, and the kitty keyboard protocol). Called when the
        /// shell signals a fresh prompt so a TUI that exited uncleanly — Ctrl+C, crash, or
        /// output dropped during PTY teardown — can't leave mouse reporting on and flood the
        /// prompt with ESC[&lt;..M reports on every pointer move. Shell-owned modes (bracketed
        /// paste, application cursor keys, auto-wrap) are intentionally left untouched.
        ///
        /// Non-blocking review note (#277): kitty itself does not reset its keyboard stacks on
        /// this kind of transient signal, and doing so is a policy call, not a spec requirement.
        /// But this method already makes the opposite policy call for mouse mode with exactly
        /// the same justification ("a TUI that exited uncleanly can't leave X reporting on") -
        /// leaving disambiguate on would have the shell prompt's Ctrl+C send
        /// <c>CSI 99;5u</c> instead of raising SIGINT, and readline would print garbage instead
        /// of interrupting. Consistency with the mouse-mode policy wins here.
        /// </summary>
        public void ResetTransientInputReporting()
        {
            MouseModeX10 = false;
            MouseModeButtonEvent = false;
            MouseModeAnyEvent = false;
            MouseModeSGR = false;
            IsFocusEventReporting = false;
            KittyKeyboard.Reset();
        }

        public ModeState Clone()
        {
            return new ModeState
            {
                MouseModeX10 = this.MouseModeX10,
                MouseModeButtonEvent = this.MouseModeButtonEvent,
                MouseModeAnyEvent = this.MouseModeAnyEvent,
                MouseModeSGR = this.MouseModeSGR,
                IsApplicationCursorKeys = this.IsApplicationCursorKeys,
                IsAutoWrapMode = this.IsAutoWrapMode,
                IsOriginMode = this.IsOriginMode,
                IsFocusEventReporting = this.IsFocusEventReporting,
                IsBracketedPasteMode = this.IsBracketedPasteMode,
                IsCursorVisible = this.IsCursorVisible,
                IsCursorBlinkEnabled = this.IsCursorBlinkEnabled,
                CursorStyle = this.CursorStyle,
                IsInsertMode = this.IsInsertMode,
                IsLineFeedNewLineMode = this.IsLineFeedNewLineMode,
                IsEchoEnabled = this.IsEchoEnabled,
                KittyKeyboard = this.KittyKeyboard.Clone()
            };
        }
    }
}
