using Xunit;
using NovaTerminal.Core;
using Avalonia.Media;
using System.Linq;

namespace NovaTerminal.Tests
{
    public class SgrAttributeTests
    {
        private TerminalCell GetCellSafe(TerminalBuffer buffer, int col, int row)
        {
            buffer.Lock.EnterReadLock();
            try { return buffer.GetCell(col, row); }
            finally { buffer.Lock.ExitReadLock(); }
        }

        [Fact]
        public void Sgr_Italic_SetsAttribute()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Enable Italic
            parser.Process("\x1b[3m");
            Assert.True(buffer.IsItalic);

            // Write char
            parser.Process("A");
            var cell = GetCellSafe(buffer, 0, 0);
            Assert.True(cell.IsItalic);

            // Disable Italic
            parser.Process("\x1b[23m");
            Assert.False(buffer.IsItalic);

            // Write another char
            parser.Process("B");
            var cell2 = GetCellSafe(buffer, 1, 0);
            Assert.False(cell2.IsItalic);
        }

        [Fact]
        public void Sgr_Underline_SetsAttribute()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[4m");
            Assert.True(buffer.IsUnderline);

            parser.Process("A");
            Assert.True(GetCellSafe(buffer, 0, 0).IsUnderline);

            parser.Process("\x1b[24m");
            Assert.False(buffer.IsUnderline);
        }

        [Fact]
        public void Sgr_Blink_SetsAttribute()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[5m");
            Assert.True(buffer.IsBlink);

            parser.Process("A");
            Assert.True(GetCellSafe(buffer, 0, 0).IsBlink);

            parser.Process("\x1b[25m");
            Assert.False(buffer.IsBlink);
        }

        [Fact]
        public void Sgr_Faint_SetsAttribute()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[2m");
            Assert.True(buffer.IsFaint);

            parser.Process("A");
            Assert.True(GetCellSafe(buffer, 0, 0).IsFaint);

            // Faint is cleared by Normal Intensity (22)
            parser.Process("\x1b[22m");
            Assert.False(buffer.IsFaint);
            Assert.False(buffer.IsBold); // Should also ensure bold is false
        }

        [Fact]
        public void Sgr_Strikethrough_SetsAttribute()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[9m");
            Assert.True(buffer.IsStrikethrough);

            parser.Process("A");
            Assert.True(GetCellSafe(buffer, 0, 0).IsStrikethrough);

            parser.Process("\x1b[29m");
            Assert.False(buffer.IsStrikethrough);
        }

        [Fact]
        public void Sgr_UnderlineColonVariant_DoesNotSetFaint()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // 4:2 means underline style (double) in SGR subparameters.
            // We currently map styles to underline on/off, but must not treat ":2" as faint.
            parser.Process("\x1b[4:2mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.True(cell.IsUnderline);
            Assert.False(cell.IsFaint);
            Assert.True(buffer.IsUnderline);
            Assert.False(buffer.IsFaint);
        }

        [Fact]
        public void Sgr_ForegroundTrueColorColonForm_DoesNotLeakTrailingComponents()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Without correct colon parsing, trailing RGB components can be interpreted
            // as standalone SGR codes (e.g. 4 => underline).
            parser.Process("\x1b[38:2::1:2:4mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(cell.IsBlink);
            Assert.False(buffer.IsUnderline);
            Assert.False(buffer.IsBlink);
        }

        [Fact]
        public void Sgr_UnderlineColorColonForm_DoesNotMutateTextAttributes()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // 58 sets underline color. Even if unsupported visually, params must be consumed.
            parser.Process("\x1b[58:2::4:5:6mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(cell.IsBlink);
            Assert.False(cell.IsFaint);
            Assert.False(buffer.IsUnderline);
            Assert.False(buffer.IsBlink);
            Assert.False(buffer.IsFaint);
        }

        [Fact]
        public void Csi_GreaterThan_M_IsNotTreatedAsSgr()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // XTerm-style key modifier control sequence (CSI > Pp ; Pv m).
            // This is not SGR and must not toggle text attributes.
            parser.Process("\x1b[>4;1mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(cell.IsBold);
            Assert.False(buffer.IsUnderline);
            Assert.False(buffer.IsBold);
        }

        [Fact]
        public void Csi_WithIntermediate_M_IsNotTreatedAsSgr()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // CSI 1 $ m contains an intermediate byte ('$').
            // It must not be routed to SGR.
            parser.Process("\x1b[1$mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsBold);
            Assert.False(cell.IsUnderline);
            Assert.False(buffer.IsBold);
            Assert.False(buffer.IsUnderline);
        }

        [Fact]
        public void Csi_FourDollarM_IsNotTreatedAsSgr()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[4$mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(cell.IsBold);
            Assert.False(buffer.IsUnderline);
            Assert.False(buffer.IsBold);
        }

        [Fact]
        public void Csi_FourSemicolonOneDollarM_IsNotTreatedAsSgr()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[4;1$mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(cell.IsBold);
            Assert.False(buffer.IsUnderline);
            Assert.False(buffer.IsBold);
        }

        [Fact]
        public void Csi_FourSpaceDollarM_IsNotTreatedAsSgr()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[4 $mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(cell.IsBold);
            Assert.False(buffer.IsUnderline);
            Assert.False(buffer.IsBold);
        }

        [Fact]
        public void Csi_SpaceBeforeM_IsNotTreatedAsSgr()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[4 mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(buffer.IsUnderline);
        }

        [Fact]
        public void Csi_PrivateLeaderM_IsNotTreatedAsSgr()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[?4mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(buffer.IsUnderline);
        }

        [Fact]
        public void Csi_LongLeaderPrefixedM_IsNotTreatedAsSgr()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[>0;1;2;3;4;5mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsBold);
            Assert.False(cell.IsUnderline);
            Assert.False(buffer.IsBold);
            Assert.False(buffer.IsUnderline);
        }

        [Fact]
        public void Osc_LongPayload_DoesNotCrashOrMutateStyle()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[0m");
            string payload = new string('x', 2048);
            parser.Process($"\x1b]999;{payload}\x07");
            parser.Process("A");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.Equal('A', cell.Character);
            Assert.False(buffer.IsBold);
            Assert.False(buffer.IsUnderline);
            Assert.False(buffer.IsItalic);
            Assert.False(buffer.IsInverse);
            Assert.False(buffer.IsStrikethrough);
            Assert.False(buffer.IsFaint);
        }

        [Fact]
        public void UnknownCsiPattern_DoesNotMutateSgrState()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[1;4m");
            Assert.True(buffer.IsBold);
            Assert.True(buffer.IsUnderline);

            // Unknown CSI pattern with intermediate and non-style final.
            parser.Process("\x1b[12$z");

            parser.Process("A");
            var cell = GetCellSafe(buffer, 0, 0);
            Assert.True(cell.IsBold);
            Assert.True(cell.IsUnderline);
            Assert.True(buffer.IsBold);
            Assert.True(buffer.IsUnderline);
        }

        [Fact]
        public void Sgr_LongParameterList_DoesNotDropTrailingResetCodes()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // If CSI param parsing truncates long lists, trailing 24 can be dropped,
            // leaving underline stuck on following text.
            string filler = string.Join(';', Enumerable.Repeat("31", 40));
            parser.Process($"\x1b[4;{filler};24mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(buffer.IsUnderline);
        }

        [Fact]
        public void Sgr_VeryLongParameterList_OverBufferLimit_DoesNotDropTrailingResetCodes()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Exercise CSI parameter input longer than legacy fixed parser buffer (256 chars).
            string filler = string.Join(';', Enumerable.Repeat("31", 180));
            parser.Process($"\x1b[4;{filler};24mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(buffer.IsUnderline);
        }

        [Fact]
        public void Sgr_ExtremeLongParameterList_DoesNotDropTrailingResetCodes()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Covers CSI payloads longer than conservative parser caps.
            string filler = string.Join(';', Enumerable.Repeat("31", 2500));
            parser.Process($"\x1b[4;{filler};24mA");

            var cell = GetCellSafe(buffer, 0, 0);
            Assert.False(cell.IsUnderline);
            Assert.False(buffer.IsUnderline);
        }

        [Fact]
        public void Sgr_Reset_ClearsAllAttributes()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // Set all
            parser.Process("\x1b[1;2;3;4;5;7;8;9m"); // Bold, Faint, Italic, Underline, Blink, Inverse, Hidden, Strikethrough

            Assert.True(buffer.IsBold);
            Assert.True(buffer.IsFaint);
            Assert.True(buffer.IsItalic);
            Assert.True(buffer.IsUnderline);
            Assert.True(buffer.IsBlink);
            Assert.True(buffer.IsInverse);
            Assert.True(buffer.IsHidden);
            Assert.True(buffer.IsStrikethrough);

            // Reset (0)
            parser.Process("\x1b[0m");

            Assert.False(buffer.IsBold);
            Assert.False(buffer.IsFaint);
            Assert.False(buffer.IsItalic);
            Assert.False(buffer.IsUnderline);
            Assert.False(buffer.IsBlink);
            Assert.False(buffer.IsInverse);
            Assert.False(buffer.IsHidden);
            Assert.False(buffer.IsStrikethrough);
        }

        [Fact]
        public void Snapshot_PropagatesSgrAttributes()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[3;4mTest"); // Italic + Underline

            RenderRowSnapshot snapshot = default;
            buffer.Lock.EnterReadLock();
            try
            {
                snapshot = buffer.GetRowSnapshot(0, 80);
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }
            var cellSnapshot = snapshot.Cells[0]; // 'T'

            Assert.Equal('T', cellSnapshot.Character);
            Assert.True(cellSnapshot.IsItalic);
            Assert.True(cellSnapshot.IsUnderline);
            Assert.False(cellSnapshot.IsBold);
        }
    }
}
