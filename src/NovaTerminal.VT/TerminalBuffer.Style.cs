namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
        private void SyncPackedState()
        {
            if (!_isStyleDirty) return;

            _packedFg = IsDefaultForeground ? Theme.Foreground.ToUint() : (CurrentFgIndex >= 0 ? (uint)CurrentFgIndex : CurrentForeground.ToUint());
            _packedBg = IsDefaultBackground ? Theme.Background.ToUint() : (CurrentBgIndex >= 0 ? (uint)CurrentBgIndex : CurrentBackground.ToUint());

            ushort f = (ushort)TerminalCellFlags.Dirty;
            if (IsBold) f |= (ushort)TerminalCellFlags.Bold;
            if (IsItalic) f |= (ushort)TerminalCellFlags.Italic;
            if (IsInverse) f |= (ushort)TerminalCellFlags.Inverse;
            if (IsUnderline) f |= (ushort)TerminalCellFlags.Underline;
            if (IsStrikethrough) f |= (ushort)TerminalCellFlags.Strikethrough;
            if (IsBlink) f |= (ushort)TerminalCellFlags.Blink;
            if (IsFaint) f |= (ushort)TerminalCellFlags.Faint;
            if (IsHidden) f |= (ushort)TerminalCellFlags.Hidden;
            if (IsDefaultForeground) f |= (ushort)TerminalCellFlags.DefaultForeground;
            if (IsDefaultBackground) f |= (ushort)TerminalCellFlags.DefaultBackground;
            if (CurrentFgIndex >= 0) f |= (ushort)TerminalCellFlags.PaletteForeground;
            if (CurrentBgIndex >= 0) f |= (ushort)TerminalCellFlags.PaletteBackground;

            _packedFlags = f;
            _isStyleDirty = false;
        }

        public TermColor CurrentForeground { get => _currentForeground; set { _currentForeground = value; _isStyleDirty = true; _isDefaultForeground = false; } }

        public TermColor CurrentBackground { get => _currentBackground; set { _currentBackground = value; _isStyleDirty = true; _isDefaultBackground = false; } }

        public short CurrentFgIndex { get => _currentFgIndex; set { _currentFgIndex = value; _isStyleDirty = true; if (value >= 0) _isDefaultForeground = false; } }

        public short CurrentBgIndex { get => _currentBgIndex; set { _currentBgIndex = value; _isStyleDirty = true; if (value >= 0) _isDefaultBackground = false; } }

        public bool IsDefaultForeground { get => _isDefaultForeground; set { _isDefaultForeground = value; _isStyleDirty = true; } }

        public bool IsDefaultBackground { get => _isDefaultBackground; set { _isDefaultBackground = value; _isStyleDirty = true; } }

        public TerminalTheme Theme { get; set; } = new TerminalTheme();

        public bool IsInverse { get => _isInverse; set { _isInverse = value; _isStyleDirty = true; } }

        public bool IsBold { get => _isBold; set { _isBold = value; _isStyleDirty = true; } }

        public bool IsFaint { get => _isFaint; set { _isFaint = value; _isStyleDirty = true; } }

        public bool IsItalic { get => _isItalic; set { _isItalic = value; _isStyleDirty = true; } }

        public bool IsUnderline { get => _isUnderline; set { _isUnderline = value; _isStyleDirty = true; } }

        public bool IsBlink { get => _isBlink; set { _isBlink = value; _isStyleDirty = true; } }

        public bool IsStrikethrough { get => _isStrikethrough; set { _isStrikethrough = value; _isStyleDirty = true; } }

        public bool IsHidden { get => _isHidden; set { _isHidden = value; _isStyleDirty = true; } }

        public string? CurrentHyperlink
        {
            get => _currentHyperlink;
            set => _currentHyperlink = value;
        }

        // Pending Wrap State (M1.3)
        public bool IsPendingWrap
        {
            get => _isPendingWrap;
            set => _isPendingWrap = value;
        }
    }
}
