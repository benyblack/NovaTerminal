using System;

namespace NovaTerminal.Core
{
    public partial class TerminalBuffer
    {
        private readonly TerminalBufferState _state = new();

        // Cursor and write-path tracking state
        private int _cursorCol { get => _state.CursorCol; set => _state.CursorCol = value; }
        private int _cursorRow { get => _state.CursorRow; set => _state.CursorRow = value; }
        private int _prevCursorCol { get => _state.PrevCursorCol; set => _state.PrevCursorCol = value; }
        private int _prevCursorRow { get => _state.PrevCursorRow; set => _state.PrevCursorRow = value; }
        private int _maxColThisRow { get => _state.MaxColThisRow; set => _state.MaxColThisRow = value; }

        // Grapheme and packed style cache state
        private char? _highSurrogateBuffer { get => _state.HighSurrogateBuffer; set => _state.HighSurrogateBuffer = value; }
        private int _lastCharCol { get => _state.LastCharCol; set => _state.LastCharCol = value; }
        private int _lastCharRow { get => _state.LastCharRow; set => _state.LastCharRow = value; }
        private bool _isAfterZwj { get => _state.IsAfterZwj; set => _state.IsAfterZwj = value; }
        private uint _packedFg { get => _state.PackedFg; set => _state.PackedFg = value; }
        private uint _packedBg { get => _state.PackedBg; set => _state.PackedBg = value; }
        private ushort _packedFlags { get => _state.PackedFlags; set => _state.PackedFlags = value; }
        private bool _isStyleDirty { get => _state.IsStyleDirty; set => _state.IsStyleDirty = value; }

        // Current SGR/style state
        private TermColor _currentForeground { get => _state.CurrentForeground; set => _state.CurrentForeground = value; }
        private TermColor _currentBackground { get => _state.CurrentBackground; set => _state.CurrentBackground = value; }
        private short _currentFgIndex { get => _state.CurrentFgIndex; set => _state.CurrentFgIndex = value; }
        private short _currentBgIndex { get => _state.CurrentBgIndex; set => _state.CurrentBgIndex = value; }
        private bool _isDefaultForeground { get => _state.IsDefaultForeground; set => _state.IsDefaultForeground = value; }
        private bool _isDefaultBackground { get => _state.IsDefaultBackground; set => _state.IsDefaultBackground = value; }
        private bool _isInverse { get => _state.IsInverse; set => _state.IsInverse = value; }
        private bool _isBold { get => _state.IsBold; set => _state.IsBold = value; }
        private bool _isFaint { get => _state.IsFaint; set => _state.IsFaint = value; }
        private bool _isItalic { get => _state.IsItalic; set => _state.IsItalic = value; }
        private bool _isUnderline { get => _state.IsUnderline; set => _state.IsUnderline = value; }
        private bool _isBlink { get => _state.IsBlink; set => _state.IsBlink = value; }
        private bool _isStrikethrough { get => _state.IsStrikethrough; set => _state.IsStrikethrough = value; }
        private bool _isHidden { get => _state.IsHidden; set => _state.IsHidden = value; }
        private string? _currentHyperlink { get => _state.CurrentHyperlink; set => _state.CurrentHyperlink = value; }
        private bool _isPendingWrap { get => _state.IsPendingWrap; set => _state.IsPendingWrap = value; }

        // Output synchronization state
        private bool _isSynchronizedOutput { get => _state.IsSynchronizedOutput; set => _state.IsSynchronizedOutput = value; }
        private DateTime _lastSyncStart { get => _state.LastSyncStart; set => _state.LastSyncStart = value; }

        private sealed class TerminalBufferState
        {
            public int CursorCol;
            public int CursorRow;
            public int PrevCursorCol;
            public int PrevCursorRow;
            public int MaxColThisRow;

            public char? HighSurrogateBuffer;
            public int LastCharCol = -1;
            public int LastCharRow = -1;
            public bool IsAfterZwj;
            public uint PackedFg;
            public uint PackedBg;
            public ushort PackedFlags;
            public bool IsStyleDirty = true;

            public TermColor CurrentForeground = TermColor.LightGray;
            public TermColor CurrentBackground = TermColor.Black;
            public short CurrentFgIndex = -1;
            public short CurrentBgIndex = -1;
            public bool IsDefaultForeground = true;
            public bool IsDefaultBackground = true;
            public bool IsInverse;
            public bool IsBold;
            public bool IsFaint;
            public bool IsItalic;
            public bool IsUnderline;
            public bool IsBlink;
            public bool IsStrikethrough;
            public bool IsHidden;
            public string? CurrentHyperlink;
            public bool IsPendingWrap;

            public bool IsSynchronizedOutput;
            public DateTime LastSyncStart;
        }
    }
}
