using System;
using NovaTerminal.Shell;
using Avalonia.Headless.XUnit;
using NovaTerminal.Platform;
using NovaTerminal.Rendering;
using NovaTerminal.Tests.Infra;
using NovaTerminal.VT;
using SkiaSharp;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    /// <summary>
    /// #234: the per-glyph draw path enumerated <em>runes</em> while calling the result "grapheme", so
    /// every multi-codepoint sequence was rasterized in pieces and its composed glyph never produced —
    /// a keycap as digit + selector + enclosing mark, a flag as two letter tiles, a ZWJ family as
    /// separate people.
    ///
    /// The observable used here is the glyph atlas entry count, because it is exactly what the loop
    /// drives and it does not depend on the CI runner having any particular emoji font: a cluster asked
    /// for once produces one entry whether or not the typeface can draw it, while a cluster split into
    /// three runes asks three times.
    /// </summary>
    [Trait("Category", "RenderMetrics")]
    public sealed class GraphemeClusterDrawPathTests
    {
        private static readonly CellMetrics Metrics = new()
        {
            CellWidth = 8.4f,
            CellHeight = 18.0f,
            Baseline = 14.0f,
            Ascent = 14.0f,
            Descent = 4.0f
        };

        private const int Cols = 12;
        private const int Rows = 2;

        /// <summary>
        /// Atlas entries produced by rendering <paramref name="content"/> into one row.
        /// </summary>
        /// <remarks>
        /// A blank buffer yields zero: whitespace-only runs are drawn directly and never reach the
        /// atlas. So a single-cell payload plus the trailing blanks yields one entry for the payload's
        /// cluster and one for the space that shares its run — two in total, whatever the payload is.
        /// </remarks>
        private static int AtlasEntriesFor(string content, bool enableComplexShaping)
        {
            var buffer = new TerminalBuffer(Cols, Rows);
            var parser = new AnsiParser(buffer);
            parser.Process(content);

            using var glyphCache = new GlyphCache();
            using var bitmap = SnapshotService.Capture(
                buffer,
                Metrics,
                (int)(Cols * Metrics.CellWidth),
                (int)(Rows * Metrics.CellHeight),
                new SnapshotCaptureOptions
                {
                    HideCursor = true,
                    EnableComplexShaping = enableComplexShaping,
                    GlyphCache = glyphCache
                });

            return glyphCache.EntryCount;
        }

        [AvaloniaTheory]
        // Keycap: "1" + VS16 + COMBINING ENCLOSING KEYCAP.
        [InlineData("1️⃣", "keycap")]
        // Flag: two regional indicators.
        [InlineData("\U0001F1FA\U0001F1F8", "regional indicator pair")]
        // ZWJ family: three people joined by ZWJ.
        [InlineData("\U0001F468‍\U0001F469‍\U0001F467", "ZWJ sequence")]
        // Skin-tone modifier.
        [InlineData("\U0001F44D\U0001F3FF", "emoji modifier sequence")]
        // VS16-promoted emoji.
        [InlineData("❤️", "VS16 sequence")]
        public void MultiCodepointSequence_CostsOneGlyph_WhenComplexShapingIsDisabled(string sequence, string description)
        {
            // `EnableComplexShaping` is a user-facing toggle. With it on, all of these are diverted to
            // the shaper and never reach the per-glyph loop, which is why the bug stayed hidden; with it
            // off, this loop is the only thing drawing them.
            int baseline = AtlasEntriesFor("A", enableComplexShaping: false);
            int actual = AtlasEntriesFor(sequence, enableComplexShaping: false);

            Assert.Equal(2, baseline); // 'A' and the space beside it - sanity on the measurement itself.
            Assert.Equal(baseline, actual);
        }

        [AvaloniaFact]
        public void CombiningMark_CostsOneGlyph_EvenInTheDefaultConfiguration()
        {
            // #234 claimed the default configuration was unaffected because emoji reach the shaper.
            // That is not true for combining marks: `ContainsRunesRequiringComplexShaping` covers
            // U+0590-U+0FFF and the emoji ranges but not U+0300-U+036F, so "a + combining acute" fell
            // through to the per-rune loop with shaping *on* as well, and was drawn as two glyphs
            // stacked at one origin. Measured, not assumed - this asserts the default path.
            int baseline = AtlasEntriesFor("A", enableComplexShaping: true);
            int actual = AtlasEntriesFor("á", enableComplexShaping: true);

            Assert.Equal(2, baseline);
            Assert.Equal(baseline, actual);
        }

        [AvaloniaFact]
        public void PlainAsciiRun_IsUnaffected()
        {
            // Guards against the enumerator change altering the ordinary case: two distinct letters
            // plus the space is three entries, exactly as before.
            Assert.Equal(3, AtlasEntriesFor("AB", enableComplexShaping: false));
        }

        [AvaloniaTheory]
        // A ZWJ family is 2 columns as a cluster but 6 as runes (three 2-wide people, the ZWJs
        // zero-width), so the old arithmetic overshot by four columns.
        [InlineData("\U0001F468‍\U0001F469‍\U0001F467")]
        // A VS16 sequence is 2 columns as a cluster but 1 as runes (U+2764 is narrow on its own and
        // the selector is zero-width), so the old arithmetic undershot. Both directions matter: a fix
        // that merely widened something would pass one of these and fail the other.
        [InlineData("❤️")]
        public void UnderlineSpansTheClusterWidth_NotTheSumOfRuneWidths(string sequence)
        {
            // The half of the defect that is not about glyph composition. The run's total width is
            // summed from each cell's *cluster* width, but `TryGetUnderlineBounds` walked runes, so the
            // two disagreed for any cluster whose rune widths do not add up to its cluster width.
            //
            // Measured by diffing the same content with and without SGR 4, which isolates the underline
            // exactly and depends on no glyph metrics at all.
            float span = MeasureUnderlineSpan(sequence);
            float letterSpan = MeasureUnderlineSpan("A");

            // The control: a 1-column cell must land in the 1-column band, otherwise the assertion
            // below would be measuring something other than what it claims.
            Assert.InRange(letterSpan, Metrics.CellWidth * 0.5f, Metrics.CellWidth * 1.5f);

            Assert.InRange(span, Metrics.CellWidth * 1.5f, Metrics.CellWidth * 2.5f);
        }

        /// <summary>Width in pixels of the underline drawn beneath <paramref name="content"/>.</summary>
        private static float MeasureUnderlineSpan(string content)
        {
            using var withRule = Render("\u001b[4m" + content);
            using var without = Render(content);

            int minX = int.MaxValue;
            int maxX = int.MinValue;
            for (int y = 0; y < withRule.Height; y++)
            {
                for (int x = 0; x < withRule.Width; x++)
                {
                    if (withRule.GetPixel(x, y) != without.GetPixel(x, y))
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                    }
                }
            }

            Assert.True(maxX >= minX, $"no underline pixels found for '{content}'");
            return maxX - minX + 1;
        }

        private static SKBitmap Render(string content)
        {
            var buffer = new TerminalBuffer(Cols, Rows);
            var parser = new AnsiParser(buffer);
            parser.Process(content);

            return SnapshotService.Capture(
                buffer,
                Metrics,
                (int)(Cols * Metrics.CellWidth),
                (int)(Rows * Metrics.CellHeight),
                new SnapshotCaptureOptions
                {
                    HideCursor = true,
                    EnableComplexShaping = false
                });
        }
    }
}
