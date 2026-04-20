using System;
using Xunit;
using NovaTerminal.Core;

namespace NovaTerminal.Tests
{
    public class DecModeTests
    {
        private TerminalCell GetCellSafe(TerminalBuffer buffer, int col, int row)
        {
            buffer.Lock.EnterReadLock();
            try { return buffer.GetCell(col, row); }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [Fact]
        public void BracketedPaste_SetsModeFlag()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            Assert.False(buffer.Modes.IsBracketedPasteMode);

            // Enable ?2004
            parser.Process("\x1b[?2004h");
            Assert.True(buffer.Modes.IsBracketedPasteMode);

            // Disable ?2004
            parser.Process("\x1b[?2004l");
            Assert.False(buffer.Modes.IsBracketedPasteMode);
        }

        [Fact]
        public void CursorVisibility_SetsModeFlag()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Default is true (usually)
            Assert.True(buffer.IsCursorVisible);

            // Hide ?25l
            parser.Process("\x1b[?25l");
            Assert.False(buffer.IsCursorVisible);
            Assert.False(buffer.Modes.IsCursorVisible);

            // Show ?25h
            parser.Process("\x1b[?25h");
            Assert.True(buffer.IsCursorVisible);
            Assert.True(buffer.Modes.IsCursorVisible);
        }

        [Fact]
        public void CursorVisibility_HideThenShowInSingleChunk_ArmsTransientSuppression()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[?25lThinking...\x1b[?25h");

            Assert.True(buffer.Modes.IsCursorVisible);

            long nowTicks = DateTime.UtcNow.Ticks;
            buffer.Lock.EnterReadLock();
            try
            {
                Assert.True(buffer.CursorSuppressedUntilUtcTicks > nowTicks);
            }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [Fact]
        public void KeypadModeEscapes_DoNotRenderLiteralCharacters()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("Prompt");
            parser.Process("\x1b=");
            parser.Process("\x1b>");

            Assert.Equal("Prompt", GetVisiblePlainText(buffer).Trim());
            Assert.Equal(6, buffer.CursorCol);
            Assert.Equal(0, buffer.CursorRow);
        }

        [Fact]
        public void CursorVisibility_HideAndShowInSeparateChunks_DoesNotArmTransientSuppression()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[?25l");
            parser.Process("\x1b[?25h");

            Assert.True(buffer.Modes.IsCursorVisible);

            long nowTicks = DateTime.UtcNow.Ticks;
            buffer.Lock.EnterReadLock();
            try
            {
                Assert.True(buffer.CursorSuppressedUntilUtcTicks <= nowTicks);
            }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [Fact]
        public void InsertMode_SetsModeFlag()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            Assert.False(buffer.Modes.IsInsertMode);

            // Enable IRM (4h)
            parser.Process("\x1b[4h");
            Assert.True(buffer.Modes.IsInsertMode);

            // Disable IRM (4l)
            parser.Process("\x1b[4l");
            Assert.False(buffer.Modes.IsInsertMode);
        }

        [Fact]
        public void InsertMode_InsertsCharacters()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Write initial text "ABC"
            parser.Process("ABC");
            Assert.Equal('A', GetCellSafe(buffer, 0, 0).Character);
            Assert.Equal('B', GetCellSafe(buffer, 1, 0).Character);
            Assert.Equal('C', GetCellSafe(buffer, 2, 0).Character);

            // Move cursor back to 'B' (index 1)
            buffer.SetCursorPosition(1, 0);

            // Enable Insert Mode
            parser.Process("\x1b[4h");

            // Write 'X'
            parser.Process("X");

            // Expect "AXBC"
            Assert.Equal('A', GetCellSafe(buffer, 0, 0).Character);
            Assert.Equal('X', GetCellSafe(buffer, 1, 0).Character);
            Assert.Equal('B', GetCellSafe(buffer, 2, 0).Character);
            Assert.Equal('C', GetCellSafe(buffer, 3, 0).Character);

            // Disable Insert Mode
            parser.Process("\x1b[4l");

            // Write 'Y' (Overwrite)
            parser.Process("Y");

            // Expect "AXYC" (B overwritten by Y)
            Assert.Equal('A', GetCellSafe(buffer, 0, 0).Character);
            Assert.Equal('X', GetCellSafe(buffer, 1, 0).Character);
            Assert.Equal('Y', GetCellSafe(buffer, 2, 0).Character);
            Assert.Equal('C', GetCellSafe(buffer, 3, 0).Character);
        }

        [Fact]
        public void InsertMode_PushesOffScreen()
        {
            // Test that characters pushed off the right edge are lost (standard terminal behavior)
            var buffer = new TerminalBuffer(10, 1); // Small buffer
            var parser = new AnsiParser(buffer);

            // "0123456789"
            parser.Process("0123456789");

            // Move to 0
            buffer.SetCursorPosition(0, 0);

            // Enable Insert
            parser.Process("\x1b[4h");

            // Insert 'X'
            parser.Process("X");

            // "X012345678" -> '9' should be pushed off
            Assert.Equal('X', GetCellSafe(buffer, 0, 0).Character);
            Assert.Equal('0', GetCellSafe(buffer, 1, 0).Character);
            Assert.Equal('8', GetCellSafe(buffer, 9, 0).Character);
        }

        [Fact]
        public void OriginMode_CupIsRelativeToScrollRegion()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[5;10r"); // 1-based rows 5..10
            parser.Process("\x1b[?6h");   // DECOM on

            // In origin mode, 1;1 maps to ScrollTop.
            parser.Process("\x1b[1;1H");
            Assert.Equal(4, buffer.CursorRow);
            Assert.Equal(0, buffer.CursorCol);

            // Large row must clamp to scroll bottom.
            parser.Process("\x1b[999;1H");
            Assert.Equal(9, buffer.CursorRow);
        }

        [Fact]
        public void OriginMode_ResetReturnsToAbsoluteAddressing()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[5;10r"); // 1-based rows 5..10
            parser.Process("\x1b[?6h");
            Assert.True(buffer.Modes.IsOriginMode);

            // DECOM set homes to scroll top.
            Assert.Equal(4, buffer.CursorRow);
            Assert.Equal(0, buffer.CursorCol);

            parser.Process("\x1b[?6l");
            Assert.False(buffer.Modes.IsOriginMode);

            // DECOM reset homes to absolute row 0.
            Assert.Equal(0, buffer.CursorRow);
            Assert.Equal(0, buffer.CursorCol);
        }

        [Fact]
        public void FocusEventReporting_SetsModeFlag()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            Assert.False(buffer.Modes.IsFocusEventReporting);
            parser.Process("\x1b[?1004h");
            Assert.True(buffer.Modes.IsFocusEventReporting);
            parser.Process("\x1b[?1004l");
            Assert.False(buffer.Modes.IsFocusEventReporting);
        }

        [Fact]
        public void ApplicationCursorKeys_SetsModeFlag()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            Assert.False(buffer.Modes.IsApplicationCursorKeys);
            parser.Process("\x1b[?1h");
            Assert.True(buffer.Modes.IsApplicationCursorKeys);
            parser.Process("\x1b[?1l");
            Assert.False(buffer.Modes.IsApplicationCursorKeys);
        }

        [Fact]
        public void MouseReportingModes_SetModeFlags()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1006h");

            Assert.True(buffer.Modes.MouseModeX10);
            Assert.True(buffer.Modes.MouseModeButtonEvent);
            Assert.True(buffer.Modes.MouseModeAnyEvent);
            Assert.True(buffer.Modes.MouseModeSGR);

            parser.Process("\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l");

            Assert.False(buffer.Modes.MouseModeX10);
            Assert.False(buffer.Modes.MouseModeButtonEvent);
            Assert.False(buffer.Modes.MouseModeAnyEvent);
            Assert.False(buffer.Modes.MouseModeSGR);
        }

        private static string GetVisiblePlainText(TerminalBuffer buffer)
        {
            var field = typeof(TerminalBuffer).GetField("_viewport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var viewport = (TerminalRow[])field!.GetValue(buffer)!;
            return string.Join("\n", viewport.Select(GetRowText)).TrimEnd();
        }

        private static string GetRowText(TerminalRow row)
        {
            var chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
            return new string(chars).TrimEnd();
        }
    }
}
