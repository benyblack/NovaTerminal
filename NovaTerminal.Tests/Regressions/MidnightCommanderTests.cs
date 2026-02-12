using NovaTerminal.Core;
using NovaTerminal.Core.Replay;
using NovaTerminal.Tests.Tools;
using NovaTerminal.Tests.Infra;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace NovaTerminal.Tests.Regressions
{
    public class MidnightCommanderTests
    {
        private readonly string _fixturesDir;
        private readonly ITestOutputHelper _output;

        public MidnightCommanderTests(ITestOutputHelper output)
        {
            _output = output;
            string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string runDir = Path.GetDirectoryName(assemblyPath)!;
            _fixturesDir = Path.Combine(runDir, "../../../Fixtures/Replay");
            if (!Directory.Exists(_fixturesDir)) Directory.CreateDirectory(_fixturesDir);
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_Resize_ShouldReflowCorrectly()
        {
            string recPath = Path.Combine(_fixturesDir, "mc_resize.rec");
            RecordingGenerator.GenerateMidnightCommander(recPath);

            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            var runner = new ReplayRunner(recPath);

            // Initial Load
            await DeadlockDetection.RunWithTimeout(async () =>
            {
                await runner.RunAsync(async (data) =>
                {
                    parser.Process(System.Text.Encoding.UTF8.GetString(data));
                    await Task.CompletedTask;
                });
            }, 5000, "MC Initial Load");

            // Verify
            // Verify state before resize

            // Resize
            await DeadlockDetection.RunWithTimeout(async () =>
            {
                buffer.Resize(100, 30);
                await Task.CompletedTask;
            }, 2000, "MC Resize");

            Assert.Equal(100, buffer.Cols);
            Assert.Equal(30, buffer.Rows);
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_Resize_SimulateRedraw()
        {
            // 1. Setup paths
            string recPathOriginal = Path.Combine(_fixturesDir, "mc_resize.rec");
            string recPathResized = Path.Combine(_fixturesDir, "mc_100x30.rec");

            // 2. Generate artifacts
            RecordingGenerator.GenerateMidnightCommander(recPathOriginal);
            RecordingGenerator.GenerateMidnightCommander_Resized_100x30(recPathResized);

            // 3. Initialize
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            // 4. Load Original (80x24)
            var runnerOriginal = new ReplayRunner(recPathOriginal);
            await runnerOriginal.RunAsync(async (data) =>
            {
                parser.Process(System.Text.Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            });

            Assert.Contains("Midnight Commander", BufferSnapshot.Capture(buffer).Lines[11]);

            // 5. Perform Resize
            buffer.Resize(100, 30);

            // 6. Simulate PTY Response (Redraw new size)
            // This mimics the application receiving SIGWINCH and sending new ANSI codes
            var runnerResized = new ReplayRunner(recPathResized);
            await runnerResized.RunAsync(async (data) =>
            {
                parser.Process(System.Text.Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            });

            // 7. Verify New State
            var snapshot = BufferSnapshot.Capture(buffer);
            Assert.Equal(100, buffer.Cols);
            Assert.Equal(30, buffer.Rows);

            // Check for content specific to the 100x30 generation
            // The generator puts "Midnight Commander (100x30)" at row 15
            Assert.Contains("(100x30)", snapshot.Lines[14]); // 0-indexed
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_Menu_ShouldOverlayContent()
        {
            string recPath = Path.Combine(_fixturesDir, "mc_menu.rec");
            RecordingGenerator.GenerateMidnightCommander_Menu(recPath);

            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            var runner = new ReplayRunner(recPath);

            await runner.RunAsync(async (data) =>
            {
                parser.Process(System.Text.Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            });

            var snapshot = BufferSnapshot.Capture(buffer);

            // Verify Menu Content (Listing mode) is present
            // It should be at line 2 (index 1)
            string line2 = snapshot.Lines[1];
            Assert.Contains("Listing mode", line2);

            // Verify background is preserved where menu ISN'T (e.g., line 2, col 40)
            // (Snapshot lines are just strings, so we can't check color here easily, 
            // but we can check if content is overwritten or not)
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_Dialog_ShouldCenterAndObscure()
        {
            string recPath = Path.Combine(_fixturesDir, "mc_dialog.rec");
            RecordingGenerator.GenerateMidnightCommander_Dialog(recPath);

            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);
            var runner = new ReplayRunner(recPath);

            await runner.RunAsync(async (data) =>
            {
                parser.Process(System.Text.Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            });

            var snapshot = BufferSnapshot.Capture(buffer);

            // Verify Dialog Content
            Assert.Contains("Delete", snapshot.Lines[9]); // Line 10 (index 9)
            Assert.Contains("file.txt?", snapshot.Lines[10]);

            // Verify we didn't crash on color codes or layout
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_Widening_ShouldNotMergeLines()
        {
            // Reproduction of the "First two columns moved to the right" bug.
            // Scenario: 
            // 1. Buffer is 10 cols wide.
            // 2. We write exactly 10 chars (filling the line).
            // 3. We write the next line.
            // 4. We resize to 20 cols.
            // Expected: Lines remain separate (because it's TUI/Box drawing, not a paragraph).
            // Actual (Suspected): Line 1 swallows Line 2 because it thinks it's wrapped.

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Draw two lines of a "panel" border
            // Line 1: "|00000000|"
            // Line 2: "|11111111|"
            string frame = "\x1b[1;1H|00000000|\x1b[2;1H|11111111|";

            // Note: If we use absolute positioning \x1b[Line;ColH, usually Wrapped is NOT set.
            // Unless the parser sets it when writing the last char?
            parser.Process(frame);

            // Verify initial state
            var snapshot = BufferSnapshot.Capture(buffer);
            Assert.Equal("|00000000|", snapshot.Lines[0]);
            Assert.Equal("|11111111|", snapshot.Lines[1]);

            // Resize to 20
            buffer.Resize(20, 5);

            // Verify Reflow
            var newSnapshot = BufferSnapshot.Capture(buffer);

            // If correct, Line 0 will be "|00000000|" (padded spaces trimmed)
            // If bug exists (merge), Line 0 will be "|00000000||11111111|"

            Assert.Equal("|00000000|", newSnapshot.Lines[0]);
            Assert.Equal("|11111111|", newSnapshot.Lines[1]);
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_SimulateWrapped_ReflowBug()
        {
            // Scenario:
            // Lines are marked as Wrapped (for unknown reason, maybe ConPTY or edge case).
            // We want to see if Reflow ignores the wrap due to our TUI heuristic.

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Draw content
            parser.Process("\x1b[1;1H|00000000|\x1b[2;1H|11111111|");

            // Manually FORCE Wrapped=true to simulate the condition we suspect
            buffer.ViewportRows[0].IsWrapped = true;

            // Resize
            buffer.Resize(20, 5);

            var newSnapshot = BufferSnapshot.Capture(buffer);

            // Expected Behavior WITH FIX: 
            // The heuristic should detect the '|' at the end and ignore the Wrapped flag.
            // So the lines should remain separate.

            Assert.Equal("|00000000|", newSnapshot.Lines[0]);
            Assert.Equal("|11111111|", newSnapshot.Lines[1]);
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_ColoredBackground_ShouldNotMerge()
        {
            // Scenario:
            // MC often fills lines with Blue background spaces.
            // If a line ends with spaces that have a background color (TUI fill), 
            // and it wraps, we should NOT merge it.
            // Use 'x' to ensure BundleSnapshot doesn't trim it, but keep Blue BG.

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Draw content: Blue background (44m) Spaces
            // Line 1: "          " (10 spaces, Blue)
            // Line 2: "M"
            string line1 = "\x1b[44m          ";
            string line2 = "\x1b[0mM"; // Reset color for Marker
            parser.Process($"{line1}{line2}");

            // Manually FORCE Wrapped=true to simulate the condition we suspect
            buffer.ViewportRows[0].IsWrapped = true;

            // Resize
            buffer.Resize(20, 5);

            var newSnapshot = BufferSnapshot.Capture(buffer);

            // Expected Behavior WITH NEW HEURISTIC: No Merge
            // Current behavior (Merge): "          M" -> Snapshot trims? No, "          M" has no trailing spaces.
            // Desired: Line 0 is empty (spaces trimmed), Line 1 is "M"

            Assert.Equal("", newSnapshot.Lines[0]);
            Assert.Equal("M", newSnapshot.Lines[1]);
        }
        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_BlockElement_ShouldNotMerge()
        {
            // Scenario:
            // MC uses block elements (U+2588 Full Block, etc.) for scrollbars/shadows.
            // If a line ends with these, it's part of the TUI structure and shouldn't wrap/merge.

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Draw content: Line ends with Full Block
            // "000000000█"
            parser.Process("\x1b[1;1H000000000\u2588\x1b[2;1H111111111\u2588");

            // Manually FORCE Wrapped=true
            buffer.ViewportRows[0].IsWrapped = true;

            // Resize
            buffer.Resize(20, 5);

            var newSnapshot = BufferSnapshot.Capture(buffer);

            // Expected Behavior WITH NEW HEURISTIC: No Merge
            // Current behavior: Merge
            Assert.Equal("000000000\u2588", newSnapshot.Lines[0]);
            Assert.Equal("111111111\u2588", newSnapshot.Lines[1]);
        }
        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_ScrollIndicator_ShouldNotMerge()
        {
            // Scenario:
            // MC uses '>' (ASCII 62) to indicate content overflow.
            // If the buffer width is exactly reached, and the line ends with '>', 
            // the heuristics might skip it unless specifically handled.

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Draw content: Line ends with '>'
            // "000000000>"
            parser.Process("\x1b[1;1H000000000>\x1b[2;1H111111111>");

            // Manually FORCE Wrapped=true
            buffer.ViewportRows[0].IsWrapped = true;

            // Resize
            buffer.Resize(20, 5);

            var newSnapshot = BufferSnapshot.Capture(buffer);

            // Expected Behavior WITH FIX: No Merge
            Assert.Equal("000000000>", newSnapshot.Lines[0]);
            Assert.Equal("111111111>", newSnapshot.Lines[1]);
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_FullWidthBorder_ShouldNotMerge()
        {
            // Scenario:
            // In CJK or WSL contexts, MC might use Fullwidth forms (U+FFxx) for borders.
            // Specifically U+FF5C (Fullwidth Vertical Line) instead of '|'.
            // This is "2 columns of char" wide (IsWide=true).

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Draw content: Line ends with Fullwidth Pipe \uFF5C
            // "000000000｜" (Last char is wide, usually takes 2 cells, but let's assume it fits or wraps)
            // Note: If buffer is 10, and we write a wide char at 9..10?
            // Let's write 8 chars + 1 Wide char = 10 columns.

            parser.Process("\x1b[1;1H00000000\uff5c\x1b[2;1H11111111\uff5c");

            // Manually FORCE Wrapped=true
            buffer.ViewportRows[0].IsWrapped = true;

            // Resize
            buffer.Resize(20, 5);

            var newSnapshot = BufferSnapshot.Capture(buffer);

            // Expected Behavior WITH FIX: No Merge
            // Note: The snapshot string might contain the wide char.
            Assert.Contains("\uff5c", newSnapshot.Lines[0]);
            if (newSnapshot.Lines[0].Length > 10)
                throw new Exception($"MERGED! Length: {newSnapshot.Lines[0].Length}");
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task Resize_HeightOnly_ShouldPreserveLayout()
        {
            // Scenario:
            // Vertical resize (changing rows, keeping cols) should NOT trigger a text Reflow.
            // Text Reflow is destructive and relies on heuristics associated with width changes.
            // If we only change height, we should just adjust the viewport/scrollback boundary.

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Draw content:
            // Line 1: "0000000000" (Wrapped forcefully)
            // Line 2: "1111111111"
            parser.Process("\x1b[1;1H00000000001111111111");

            // Manually set Wrapped=true on Line 1 to simulate a wrapped line
            buffer.ViewportRows[0].IsWrapped = true;

            // Capture initial state
            var snapshotInitial = BufferSnapshot.Capture(buffer);
            Assert.Equal("0000000000", snapshotInitial.Lines[0]);
            Assert.Equal("1111111111", snapshotInitial.Lines[1]);

            // Resize Height Only (10x5 -> 10x10)
            buffer.Resize(10, 10);

            var snapshotAfter = BufferSnapshot.Capture(buffer);

            // Expectation:
            // 1. Content should be identical (no re-wrapping checks).
            // 2. We specifically check that Lines[0] is still "0000000000" and not merged/split.

            Assert.Equal("0000000000", snapshotAfter.Lines[0]);
            Assert.Equal("1111111111", snapshotAfter.Lines[1]);

            // Verify we have more rows now
            Assert.Equal(10, buffer.Rows);
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_TextWithBackground_ShouldReflow()
        {
            // Scenario:
            // "Hint: Want your plain shell?..."
            // "Hi" is at end of line. "nt..." on next.
            // If "Hi" has a specific background color (e.g. Black/40m instead of Default),
            // our heuristic must NOT treat it as a fixed-width TUI block. It's text. It should reflow.

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Draw content:
            // Line 1: "...Hi" (Black BG)
            // Line 2: "nt..." (Black BG)
            // Width 10.
            // Set Black BG (\x1b[40m) BEFORE writing text.
            parser.Process("\x1b[40m\x1b[1;1H12345678Hi\x1b[2;1Hnt4567890\x1b[0m");

            // Manually FORCE Wrapped=true
            buffer.ViewportRows[0].IsWrapped = true;

            // Resize Wider
            buffer.Resize(20, 5);

            var snapshot = BufferSnapshot.Capture(buffer);

            // Expectation:
            // Lines should merge because it ends in 'i' (Letter), even if BG is explicit Black.
            // "12345678Hint4567890"

            string combined = snapshot.Lines[0].Trim();
            Assert.Contains("Hint", combined);
            Assert.DoesNotContain("Hi", snapshot.Lines[0].Substring(snapshot.Lines[0].Length - 2)); // Shouldn't end with Hi
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_SortIndicator_ShouldNotReflow()
        {
            // Scenario:
            // MC sometimes uses Arrows (U+2191 '↑', U+2193 '↓') for sort indicators instead of '^'.
            // ".[↑]" or ".[↓]".
            // These should also be protected like ']' and '^'.

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Line 1: "...↑"
            // Line 2: "┐"
            // Use Blue BG to match Header style
            parser.Process("\x1b[44m\x1b[1;1H12345678↑\x1b[0m\x1b[2;1H\u2510");

            buffer.ViewportRows[0].IsWrapped = true;
            buffer.Resize(20, 5);

            var snapshot = BufferSnapshot.Capture(buffer);

            // Expectation: NO MERGE.
            Assert.Equal("12345678↑", snapshot.Lines[0].Trim());
            Assert.Equal("\u2510", snapshot.Lines[1].Trim());
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public void MC_Header_ShouldNotCombineCharacters()
        {
            // Investigate if [^] results in 3 cells or if ^ combines with ].
            var buffer = new TerminalBuffer(10, 1);
            var parser = new AnsiParser(buffer);

            // Standard MC Header sequence: ".[^]"
            // Colors usually: Blue BG.
            parser.Process("\x1b[44m.[^]\x1b[0m");

            var row = buffer.ViewportRows[0];

            // Expected:
            // Cell 0: '.'
            // Cell 1: '['
            // Cell 2: '^'
            // Cell 3: ']'

            Assert.Equal('.', row.Cells[0].Character);
            Assert.Equal('[', row.Cells[1].Character);
            Assert.Equal('^', row.Cells[2].Character);
            Assert.Equal(']', row.Cells[3].Character);

            // Also check Widths. If ^ is 0, it might be combining.
            Assert.False(row.Cells[2].IsWide);
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_CornerMerge_ShouldNotOccur()
        {
            // Scenario (User Report):
            // Header line ends with '>' (Overflow). 
            // Next line (or ghost content) starts with '┐' (Corner).
            // They should NOT merge.
            // ".[^]>" on Line 1. "┐" on Line 2.

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Line 1: "...>"
            // Line 2: "┐"
            parser.Process("\x1b[1;1H123456789>\x1b[2;1H\u2510");

            // Force wrap to simulate "I want to merge" state
            buffer.ViewportRows[0].IsWrapped = true;

            // Resize
            buffer.Resize(20, 5);

            var snapshot = BufferSnapshot.Capture(buffer);

            // Expectation:
            // Line 1 ends with '>' (Protected).
            // Line 2 starts with '┐'.
            // NO MERGE.

            Assert.Equal("123456789>", snapshot.Lines[0].Trim());
            Assert.Equal("\u2510", snapshot.Lines[1].Trim());

            // Sub-Test 2: Check ']' which is common in MC headers ".[^]"
            buffer.Resize(10, 5);
            // Use Blue Background (\x1b[44m) because our heuristic only protects ']' if it has background.
            parser.Process("\x1b[44m\x1b[1;1H123456789]\x1b[0m\x1b[2;1H\u2510");
            buffer.ViewportRows[0].IsWrapped = true;
            buffer.Resize(20, 5);

            var snapshot2 = BufferSnapshot.Capture(buffer);
            // If ']' is not protected, it might merge.
            // We want it to NOT merge if it has background? Or should it?
            // ".[^]" header usually has background.
            // For this test, we didn't set background.
            // If we rely on char check only, ']' might allow merge.
            // Let's see behavior. If it merges, we might need to protect ']' if BG is present.

            Assert.Equal("123456789]", snapshot2.Lines[0].Trim());
        }

        [Fact]
        [Trait("Category", "Regression")]
        [Trait("Target", "MidnightCommander")]
        public async Task MC_Widening_SpaceFill_ShouldNotMerge()
        {
            // Scenario:
            // Buffer width 10. Line filled with Blue Spaces. Wrapped=true.
            // Resize to 20.
            // The 'Cells' array grows to 20. The first 10 are Blue Spaces. The new 10 are '\0' (Default).
            // Current Heuristic checks Cells[Length-1] (index 19) -> '\0'.
            // It sees Default BG, so it decides "Not a TUI line", and allows Merge.
            // BUG: It merges the line with the next one, destroying the TUI layout.

            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);

            // Line 1: 10 Blue Spaces.
            // Line 2: "M" (Marker).
            string line1 = "\x1b[44m          ";
            string line2 = "\x1b[0mM";
            parser.Process($"{line1}{line2}");

            buffer.ViewportRows[0].IsWrapped = true;

            // Resize 10 -> 20
            buffer.Resize(20, 5);

            var snapshot = BufferSnapshot.Capture(buffer);

            // Expectation:
            // Heuristic should scan backwards, skip '\0', find the Blue Space at index 9.
            // Recognize it as Blue Space -> Protect.
            // NO MERGE.

            // If Merged: Line 0 is empty (trimmed)?? No.
            // If Merged: "          M" -> Trimmed "M".
            // If NOT Merged: Line 0 empty (trimmed spaces), Line 1 "M".

            // Wait, if "          " (Blue) is merged with "M".
            // Line ends up being "          M".
            // Snapshot.Lines[0].Trim() -> "M".
            // If NOT merged, Snapshot.Lines[0] is "", Lines[1] is "M".

            Assert.Equal("", snapshot.Lines[0].Trim());
            Assert.Equal("M", snapshot.Lines[1].Trim());
        }
    }
}
