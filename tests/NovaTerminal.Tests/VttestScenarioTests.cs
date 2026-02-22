using System;
using Xunit;
using NovaTerminal.Core;

namespace NovaTerminal.Tests
{
    public class VttestScenarioTests
    {
        private TerminalBuffer CreateBuffer(int cols = 80, int rows = 24)
        {
            return new TerminalBuffer(cols, rows);
        }

        private AnsiParser CreateParser(TerminalBuffer buffer, Action<string> onResponse)
        {
            return new AnsiParser(buffer)
            {
                OnResponse = onResponse
            };
        }

        private TerminalCell GetCellSafe(TerminalBuffer buffer, int col, int row)
        {
            buffer.Lock.EnterReadLock();
            try { return buffer.GetCell(col, row); }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [Fact]
        public void PrimaryDA_ShouldRespondCorrectly()
        {
            var buffer = CreateBuffer();
            string response = null;
            var parser = CreateParser(buffer, r => response = r);

            // CSI c (Primary DA)
            parser.Process("\x1b[c");

            // Expect VT220 + Sixel + ANSI Color: ?62;4;22c
            Assert.Equal("\x1b[?62;4;22c", response);
        }

        [Fact]
        public void SecondaryDA_ShouldRespondCorrectly()
        {
            var buffer = CreateBuffer();
            string response = null;
            var parser = CreateParser(buffer, r => response = r);

            // CSI > c (Secondary DA)
            parser.Process("\x1b[>c");

            // Expect VT220, Firmware 1.0: >1;10;0c
            Assert.Equal("\x1b[>1;10;0c", response);
        }

        [Fact]
        public void CursorPositionReport_ShouldReturnCorrectCoordinates()
        {
            var buffer = CreateBuffer();
            string response = null;
            var parser = CreateParser(buffer, r => response = r);

            // Move cursor to 5, 10 (0-indexed -> 1-indexed 6, 11)
            buffer.SetCursorPosition(10, 5); // col=10, row=5

            // CSI 6 n (CPR)
            parser.Process("\x1b[6n");

            Assert.Equal("\x1b[6;11R", response);
        }

        [Fact]
        public void ScreenAlignmentPattern_ShouldFillBufferWithEs()
        {
            var buffer = CreateBuffer(80, 24);
            var parser = CreateParser(buffer, _ => { });

            // ESC # 8 (DECALN)
            parser.Process("\x1b#8");

            // Verify top-left, center, bottom-right are 'E'
            Assert.Equal('E', GetCellSafe(buffer, 0, 0).Character);
            Assert.Equal('E', GetCellSafe(buffer, 40, 12).Character);
            Assert.Equal('E', GetCellSafe(buffer, 79, 23).Character);

            // Verify cursor reset
            Assert.Equal(0, buffer.CursorCol);
            Assert.Equal(0, buffer.CursorRow);
        }

        [Fact]
        public void DoubleHeightDoubleWidth_ShouldNotShiftState()
        {
            // Verify that ESC # 3/4/5/6 do NOT leave parser in weird state
            var buffer = CreateBuffer();
            var parser = CreateParser(buffer, _ => { });

            parser.Process("\x1b#3"); // Double height top (ignored)
            parser.Process("A");      // Should write 'A'

            Assert.Equal('A', GetCellSafe(buffer, 0, 0).Character);
        }
    }
}
