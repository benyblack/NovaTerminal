using System;
using System.Linq;
using System.Reflection;
using Xunit;
using NovaTerminal.Core;
using NovaTerminal.Core.Storage;

namespace NovaTerminal.Tests
{
    public class AlternateScreenTests
    {
        [Fact]
        public void AltScreen_Vim_ShouldNotPolluteScrollback()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Fill history
            for (int i = 0; i < 50; i++)
                buffer.Write($"History {i}\n");

            int initialScrollbackCount = buffer.Scrollback.Count;

            // Enter alt screen (vim)
            parser.Process("\x1b[?1049h");

            // Write vim UI
            buffer.Write("VIM UI CONTENT\n");
            buffer.Write("More vim content\n");

            // Exit alt screen
            parser.Process("\x1b[?1049l");

            // Assert: scrollback unchanged
            Assert.Equal(initialScrollbackCount, buffer.Scrollback.Count);

            // Assert: vim content not in scrollback
            var sb = buffer.Scrollback;
            var scrollbackText = "";
            for (int i = 0; i < sb.Count; i++) scrollbackText += GetTextFromSpan(sb.GetRow(i));
            Assert.DoesNotContain("VIM UI", scrollbackText);
        }

        [Fact]
        public void AltScreen_Legacy47_ShouldWork()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            buffer.Write("Main screen content\n");
            int initialScrollbackCount = buffer.Scrollback.Count;

            // Enter alt screen (legacy ?47)
            parser.Process("\x1b[?47h");
            buffer.Write("Alt screen content\n");

            // Exit alt screen
            parser.Process("\x1b[?47l");

            // Scrollback should be unchanged
            Assert.Equal(initialScrollbackCount, buffer.Scrollback.Count);
        }

        [Fact]
        public void AltScreen_1047_ShouldWork()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            buffer.Write("Main screen content\n");

            // Enter alt screen (?1047)
            parser.Process("\x1b[?1047h");
            buffer.Write("Alt screen content\n");

            // Exit alt screen
            parser.Process("\x1b[?1047l");

            // Main screen should be restored
            var viewportText = GetViewportText(buffer);
            Assert.Contains("Main screen", viewportText);
            Assert.DoesNotContain("Alt screen", viewportText);
        }

        [Fact]
        public void AltScreen_1049_ShouldSaveAndRestoreCursor()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Set cursor position and attributes
            buffer.CursorRow = 5;
            buffer.CursorCol = 10;
            buffer.IsBold = true;

            // Enter alt screen with cursor save (?1049)
            parser.Process("\x1b[?1049h");

            // Move cursor in alt screen
            buffer.CursorRow = 0;
            buffer.CursorCol = 0;
            buffer.IsBold = false;

            // Exit alt screen (should restore cursor)
            parser.Process("\x1b[?1049l");

            // Assert: cursor restored
            Assert.Equal(5, buffer.CursorRow);
            Assert.Equal(10, buffer.CursorCol);
            Assert.True(buffer.IsBold);
        }

        [Fact]
        public void AltScreen_1047_ShouldRestoreMainLiveCursorState()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            buffer.CursorRow = 6;
            buffer.CursorCol = 12;
            buffer.IsBold = true;

            parser.Process("\x1b[?1047h");

            buffer.CursorRow = 1;
            buffer.CursorCol = 2;
            buffer.IsBold = false;

            parser.Process("\x1b[?1047l");

            Assert.Equal(6, buffer.CursorRow);
            Assert.Equal(12, buffer.CursorCol);
            Assert.True(buffer.IsBold);
        }

        [Fact]
        public void AltScreen_1049_ExitVia47_ShouldRestoreSavedMainCursor()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            buffer.CursorRow = 7;
            buffer.CursorCol = 14;
            buffer.IsBold = true;

            parser.Process("\x1b[?1049h");

            buffer.CursorRow = 2;
            buffer.CursorCol = 3;
            buffer.IsBold = false;

            parser.Process("\x1b[?47l");

            Assert.Equal(7, buffer.CursorRow);
            Assert.Equal(14, buffer.CursorCol);
            Assert.True(buffer.IsBold);
        }

        [Fact]
        public void AltScreen_47_ExitVia1049_ShouldNotRestoreUnpairedSavedCursor()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            buffer.CursorRow = 4;
            buffer.CursorCol = 9;
            buffer.IsBold = true;

            parser.Process("\x1b[?47h");

            buffer.CursorRow = 0;
            buffer.CursorCol = 0;
            buffer.IsBold = false;

            parser.Process("\x1b[?1049l");

            Assert.Equal(4, buffer.CursorRow);
            Assert.Equal(9, buffer.CursorCol);
            Assert.True(buffer.IsBold);
        }

        [Fact]
        public void AltScreen_ScrollShouldNotAffectScrollback()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Fill main screen
            for (int i = 0; i < 30; i++)
                buffer.Write($"Line {i}\n");

            int scrollbackBeforeAlt = buffer.Scrollback.Count;

            // Enter alt screen
            parser.Process("\x1b[?1049h");

            // Fill alt screen (should trigger scrolling)
            for (int i = 0; i < 30; i++)
                buffer.Write($"Alt Line {i}\n");

            // Scrollback should NOT have grown
            Assert.Equal(scrollbackBeforeAlt, buffer.Scrollback.Count);

            // Exit alt screen
            parser.Process("\x1b[?1049l");

            // Scrollback still unchanged
            Assert.Equal(scrollbackBeforeAlt, buffer.Scrollback.Count);
        }

        [Fact]
        public void AltScreen_1049_ShouldRestoreExtendedSgrState()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            buffer.IsFaint = true;
            buffer.IsItalic = true;
            buffer.IsUnderline = true;
            buffer.IsBlink = true;
            buffer.IsStrikethrough = true;

            parser.Process("\x1b[?1049h");

            buffer.IsFaint = false;
            buffer.IsItalic = false;
            buffer.IsUnderline = false;
            buffer.IsBlink = false;
            buffer.IsStrikethrough = false;

            parser.Process("\x1b[?1049l");

            Assert.True(buffer.IsFaint);
            Assert.True(buffer.IsItalic);
            Assert.True(buffer.IsUnderline);
            Assert.True(buffer.IsBlink);
            Assert.True(buffer.IsStrikethrough);
        }

        private string GetTextFromRow(TerminalRow row)
        {
            if (row.Cells != null)
            {
                return GetTextFromSpan(row.Cells);
            }
            return "";
        }

        private string GetTextFromSpan(ReadOnlySpan<TerminalCell> span)
        {
            char[] chars = new char[span.Length];
            for (int i = 0; i < span.Length; i++)
            {
                chars[i] = span[i].Character == '\0' ? ' ' : span[i].Character;
            }
            return new string(chars).Trim();
        }

        private string GetViewportText(TerminalBuffer buffer)
        {
            var field = typeof(TerminalBuffer).GetField("_viewport", BindingFlags.NonPublic | BindingFlags.Instance);
            var viewport = (TerminalRow[])field!.GetValue(buffer)!;
            return string.Join("\n", viewport.Select(r => GetTextFromRow(r)));
        }
    }
}
