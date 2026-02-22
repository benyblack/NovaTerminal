namespace NovaTerminal.Core
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
                IsInsertMode = this.IsInsertMode
            };
        }
    }
}
