using System;
using Xunit;
using NovaTerminal.Core;

namespace NovaTerminal.Tests
{
    public class PendingWrapTests
    {
        private TerminalBuffer CreateBuffer(int cols = 80, int rows = 24)
        {
            var buffer = new TerminalBuffer(cols, rows);
            buffer.Modes.IsAutoWrapMode = true;
            return buffer;
        }

        private TerminalCell GetCellSafe(TerminalBuffer buffer, int col, int row)
        {
            buffer.Lock.EnterReadLock();
            try { return buffer.GetCell(col, row); }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [Fact]
        public void WriteAtLastColumn_ShouldEnablePendingWrap_AndNotWrapYet()
        {
            var buffer = CreateBuffer(10, 5);

            // Move to last column (index 9)
            buffer.SetCursorPosition(9, 0);

            // Write a character
            buffer.WriteChar('A');

            // Assert:
            // 1. Character is written at (9, 0)
            // 2. Cursor is still at (9, 0) - visually clamped
            // 3. PendingWrap is TRUE
            // 4. Next line is empty (no wrap yet)

            Assert.Equal('A', GetCellSafe(buffer, 9, 0).Character);
            Assert.Equal(9, buffer.CursorCol); // Should be clamped to 9
            Assert.Equal(0, buffer.CursorRow);
            Assert.True(buffer.IsPendingWrap, "IsPendingWrap should be true");
            Assert.Equal(' ', GetCellSafe(buffer, 0, 1).Character);
        }

        [Fact]
        public void WriteInPendingWrapState_ShouldTriggerWrap()
        {
            var buffer = CreateBuffer(10, 5);

            // Establish pending wrap state
            buffer.SetCursorPosition(9, 0);
            buffer.WriteChar('A');
            Assert.True(buffer.IsPendingWrap);

            // Write next char
            buffer.WriteChar('B');

            // Assert:
            // 1. 'B' is at (0, 1) - Wrapped
            // 2. Cursor is at (1, 1) - Advanced after write
            // 3. PendingWrap is FALSE

            Assert.Equal('B', GetCellSafe(buffer, 0, 1).Character);
            Assert.Equal(1, buffer.CursorCol);
            Assert.Equal(1, buffer.CursorRow);
            Assert.False(buffer.IsPendingWrap, "IsPendingWrap should be false after wrap");
        }

        [Fact]
        public void ExplicitMovement_ShouldResetPendingWrap()
        {
            var buffer = CreateBuffer(10, 5);

            // Establish pending wrap state
            buffer.SetCursorPosition(9, 0);
            buffer.WriteChar('A');
            Assert.True(buffer.IsPendingWrap);

            // Explicit move back to 0
            buffer.SetCursorPosition(0, 0);

            Assert.False(buffer.IsPendingWrap, "Explicit movement should reset pending wrap");
        }

        [Fact]
        public void Backspace_ShouldResetPendingWrap_AndMoveBack()
        {
            var buffer = CreateBuffer(10, 5);

            // Establish pending wrap state at (9, 0)
            buffer.SetCursorPosition(9, 0);
            buffer.WriteChar('A'); // Cursor stays at 9, PendingWrap=true

            // Backspace
            buffer.WriteChar('\b');

            // Assert:
            // 1. PendingWrap reset
            // 2. Cursor at 8 (moved back from "phantom" 10th position?)
            //    Standard VT behavior: If at right margin, BS moves to right margin - 1.
            //    Our logic: if (CursorCol > 0) CursorCol--
            //    Since CursorCol was 9 (clamped), it goes to 8.

            Assert.False(buffer.IsPendingWrap);
            Assert.Equal(8, buffer.CursorCol);
        }

        [Fact]
        public void CarriageReturn_ShouldResetPendingWrap()
        {
            var buffer = CreateBuffer(10, 5);
            buffer.SetCursorPosition(9, 0);
            buffer.WriteChar('A');
            Assert.True(buffer.IsPendingWrap);

            buffer.WriteChar('\r');

            Assert.False(buffer.IsPendingWrap);
            Assert.Equal(0, buffer.CursorCol);
        }

        [Fact]
        public void WideChar_AtMargin_ShouldWrapImmediately()
        {
            var buffer = CreateBuffer(10, 5);

            // Move to 9
            buffer.SetCursorPosition(9, 0);

            // Write wide char (width 2) -> Should wrap immediately because it doesn't fit
            // It won't trigger pending wrap because pending wrap is only if it FITS exactly.
            buffer.WriteContent("あ"); // U+3042 Hiragana A (Wide)

            // Assert:
            // 1. 'あ' is at (0, 1)
            // 2. (9, 0) is likely space (or whatever was there)
            // 3. Cursor at (2, 1)

            Assert.Equal('あ', GetCellSafe(buffer, 0, 1).Character);
            Assert.Equal(2, buffer.CursorCol);
            Assert.Equal(1, buffer.CursorRow);
        }

        [Fact]
        public void WideChar_NearMargin_ShouldEnablePendingWrap_IfFitsExactly()
        {
            var buffer = CreateBuffer(10, 5);

            // Move to 8 (Space for 2 chars: 8, 9)
            buffer.SetCursorPosition(8, 0);

            // Write wide char
            buffer.WriteContent("あ");

            // Assert:
            // 1. 'あ' at (8, 0)
            // 2. Fills 8, 9.
            // 3. Cursor at 10 -> Clamped to 9?
            //    Logic: _cursorCol (8) + width (2) == Cols (10) -> Fits exactly.
            //    Write logic writes at 8.
            //    Sets _cursorCol to 9 (clamped).
            //    Sets PendingWrap = true.

            Assert.Equal('あ', GetCellSafe(buffer, 8, 0).Character);
            Assert.Equal(9, buffer.CursorCol); // Visual cursor at last cell of wide char
            Assert.True(buffer.IsPendingWrap);

            // Next char wraps
            buffer.WriteChar('B');
            Assert.Equal('B', GetCellSafe(buffer, 0, 1).Character);
        }
    }
}
