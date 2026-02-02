using System;
using System.Linq;
using System.Reflection;
using Xunit;
using NovaTerminal.Core;

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
            
            int initialScrollbackCount = buffer.ScrollbackRows.Count;
            
            // Enter alt screen (vim)
            parser.Process("\x1b[?1049h");
            
            // Write vim UI
            buffer.Write("VIM UI CONTENT\n");
            buffer.Write("More vim content\n");
            
            // Exit alt screen
            parser.Process("\x1b[?1049l");
            
            // Assert: scrollback unchanged
            Assert.Equal(initialScrollbackCount, buffer.ScrollbackRows.Count);
            
            // Assert: vim content not in scrollback
            var scrollbackText = string.Join("", buffer.ScrollbackRows.Select(r => GetTextFromRow(r)));
            Assert.DoesNotContain("VIM UI", scrollbackText);
        }

        [Fact]
        public void AltScreen_Legacy47_ShouldWork()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            
            buffer.Write("Main screen content\n");
            int initialScrollbackCount = buffer.ScrollbackRows.Count;
            
            // Enter alt screen (legacy ?47)
            parser.Process("\x1b[?47h");
            buffer.Write("Alt screen content\n");
            
            // Exit alt screen
            parser.Process("\x1b[?47l");
            
            // Scrollback should be unchanged
            Assert.Equal(initialScrollbackCount, buffer.ScrollbackRows.Count);
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
        public void AltScreen_ScrollShouldNotAffectScrollback()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            
            // Fill main screen
            for (int i = 0; i < 30; i++)
                buffer.Write($"Line {i}\n");
            
            int scrollbackBeforeAlt = buffer.ScrollbackRows.Count;
            
            // Enter alt screen
            parser.Process("\x1b[?1049h");
            
            // Fill alt screen (should trigger scrolling)
            for (int i = 0; i < 30; i++)
                buffer.Write($"Alt Line {i}\n");
            
            // Scrollback should NOT have grown
            Assert.Equal(scrollbackBeforeAlt, buffer.ScrollbackRows.Count);
            
            // Exit alt screen
            parser.Process("\x1b[?1049l");
            
            // Scrollback still unchanged
            Assert.Equal(scrollbackBeforeAlt, buffer.ScrollbackRows.Count);
        }

        private string GetTextFromRow(TerminalRow row)
        {
            if (row.Cells != null)
            {
                var chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
                return new string(chars).Trim();
            }
            return "";
        }

        private string GetViewportText(TerminalBuffer buffer)
        {
            var field = typeof(TerminalBuffer).GetField("_viewport", BindingFlags.NonPublic | BindingFlags.Instance);
            var viewport = (TerminalRow[])field!.GetValue(buffer)!;
            return string.Join("\n", viewport.Select(r => GetTextFromRow(r)));
        }
    }
}
