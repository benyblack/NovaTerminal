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

        public ModeState Clone()
        {
            return new ModeState
            {
                MouseModeX10 = this.MouseModeX10,
                MouseModeButtonEvent = this.MouseModeButtonEvent,
                MouseModeAnyEvent = this.MouseModeAnyEvent,
                MouseModeSGR = this.MouseModeSGR,
                IsApplicationCursorKeys = this.IsApplicationCursorKeys,
                IsAutoWrapMode = this.IsAutoWrapMode
            };
        }
    }
}
