

namespace NovaTerminal.Core
{
    /// <summary>
    /// Represents the current cursor state including position and SGR attributes.
    /// </summary>
    public class CursorState
    {
        public int Row { get; set; }
        public int Col { get; set; }

        // SGR attributes
        public TermColor Foreground { get; set; } = TermColor.LightGray;
        public TermColor Background { get; set; } = TermColor.Black;
        public short FgIndex { get; set; } = -1;
        public short BgIndex { get; set; } = -1;
        public bool IsDefaultForeground { get; set; } = true;
        public bool IsDefaultBackground { get; set; } = true;
        public bool IsInverse { get; set; }
        public bool IsBold { get; set; }
        public bool IsHidden { get; set; }
        public bool IsPendingWrap { get; set; }

        public CursorState Clone()
        {
            return new CursorState
            {
                Row = this.Row,
                Col = this.Col,
                Foreground = this.Foreground,
                Background = this.Background,
                FgIndex = this.FgIndex,
                BgIndex = this.BgIndex,
                IsDefaultForeground = this.IsDefaultForeground,
                IsDefaultBackground = this.IsDefaultBackground,
                IsInverse = this.IsInverse,
                IsBold = this.IsBold,
                IsHidden = this.IsHidden,
                IsPendingWrap = this.IsPendingWrap
            };
        }
    }

    /// <summary>
    /// Stores saved cursor states for main and alternate screens.
    /// Used by DECSC/DECRC escape sequences.
    /// </summary>
    public class SavedCursorStates
    {
        public CursorState Main { get; } = new();
        public CursorState Alt { get; } = new();
    }
}
