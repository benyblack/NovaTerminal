using System.Text;
using NovaTerminal.Core;
using NovaTerminal.Core.Replay;

namespace NovaTerminal.Tests
{
    public class AnsiCorpusReplayTests
    {
        private readonly record struct ColorState(bool IsDefault, bool IsIndexed, short Index, uint Value);
        private readonly record struct StyleState(
            bool Bold,
            bool Dim,
            bool Italic,
            bool Underline,
            bool Inverse,
            bool Strike,
            ColorState Foreground,
            ColorState Background);

        private static StyleState GetStyleState(TerminalBuffer buffer)
        {
            var fg = new ColorState(
                IsDefault: buffer.IsDefaultForeground,
                IsIndexed: buffer.CurrentFgIndex >= 0,
                Index: buffer.CurrentFgIndex,
                Value: buffer.CurrentForeground.ToUint());

            var bg = new ColorState(
                IsDefault: buffer.IsDefaultBackground,
                IsIndexed: buffer.CurrentBgIndex >= 0,
                Index: buffer.CurrentBgIndex,
                Value: buffer.CurrentBackground.ToUint());

            return new StyleState(
                Bold: buffer.IsBold,
                Dim: buffer.IsFaint,
                Italic: buffer.IsItalic,
                Underline: buffer.IsUnderline,
                Inverse: buffer.IsInverse,
                Strike: buffer.IsStrikethrough,
                Foreground: fg,
                Background: bg);
        }

        private static string ResolvePath(string relativePath)
        {
            string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            string fromOutput = Path.Combine(AppContext.BaseDirectory, normalized);
            if (File.Exists(fromOutput)) return fromOutput;

            // Fallback for local runs where assets are not copied.
            string fromRepo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../", normalized));
            if (File.Exists(fromRepo)) return fromRepo;

            throw new FileNotFoundException($"Asset not found for relative path '{relativePath}'.");
        }

        private static string? TryResolvePath(string relativePath)
        {
            try
            {
                return ResolvePath(relativePath);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        private static async Task ReplayToBuffer(string relativePath, TerminalBuffer buffer, AnsiParser parser)
        {
            string recPath = ResolvePath(relativePath);
            var reader = new ReplayReader(recPath);
            await reader.RunAsync(
                onDataCallback: async data =>
                {
                    parser.Process(Encoding.UTF8.GetString(data));
                    await Task.CompletedTask;
                },
                onResizeCallback: async (cols, rows) =>
                {
                    buffer.Resize(cols, rows);
                    await Task.CompletedTask;
                });
        }

        [Theory]
        [InlineData("corpus/synthetic_opencode_startup.rec")]
        [InlineData("corpus/synthetic_btop_nav.rec")]
        [InlineData("corpus/synthetic_yazi_nav.rec")]
        [InlineData("corpus/synthetic_ranger_nav.rec")]
        public async Task ReplayCorpus_DoesNotMutateSgrUnexpectedly(string path)
        {
            var buffer = new TerminalBuffer(120, 40);
            var parser = new AnsiParser(buffer);

            // Baseline reset
            parser.Process("\x1b[0m");
            parser.Process("\x1b[1m"); // bold ON for mutation detection

            var baseline = GetStyleState(buffer);

            await ReplayToBuffer(path, buffer, parser);

            var final = GetStyleState(buffer);
            Assert.Equal(baseline, final);
        }

        [Fact]
        public void CorpusAssets_ArePresentInOutputDirectory()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "corpus", "synthetic_opencode_startup.rec");
            Assert.True(File.Exists(path), $"Missing copied corpus asset at '{path}'.");
        }

        [Fact]
        public void LeaderPrefixedSgr_IsIgnored()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[0m");
            parser.Process("\x1b[>4;1m");

            var state = GetStyleState(buffer);
            Assert.False(state.Bold);
            Assert.False(state.Underline);
        }

        [Theory]
        [InlineData("\x1b[4 m")]
        [InlineData("\x1b[?4m")]
        [InlineData("\x1b[<4m")]
        [InlineData("\x1b[=4m")]
        public void LeaderOrIntermediatePrefixedM_DoesNotMutateSgr(string sequence)
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[0m");
            parser.Process(sequence);

            var state = GetStyleState(buffer);
            Assert.False(state.Bold);
            Assert.False(state.Underline);
            Assert.False(state.Italic);
            Assert.False(state.Inverse);
        }

        [Fact]
        public void UnknownCsiPattern_DoesNotMutateSgrState()
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[0m");
            parser.Process("\x1b[1m"); // baseline bold on
            var baseline = GetStyleState(buffer);

            // Unknown/non-SGR CSI with intermediate byte.
            parser.Process("\x1b[12$z");
            parser.Process("A");

            var final = GetStyleState(buffer);
            Assert.Equal(baseline, final);
        }

        [Fact]
        public async Task ReplayCorpus_ProducesStableSnapshot()
        {
            string recPath = "corpus/synthetic_opencode_startup.rec";
            string? snapPath = TryResolvePath("corpus/synthetic_opencode_startup.snap");

            // Optional guard: if no baseline is committed yet, skip.
            if (string.IsNullOrEmpty(snapPath) || !File.Exists(snapPath)) return;

            var buffer = new TerminalBuffer(120, 40);
            var parser = new AnsiParser(buffer);
            await ReplayToBuffer(recPath, buffer, parser);

            var snapshot = BufferSnapshot.Capture(buffer, includeAttributes: true);
            GoldenMaster.AssertMatches(snapshot, snapPath);
        }

        [Fact]
        public void RandomCsiFuzz_DoesNotMutateStyleOrCrash()
        {
            var rnd = new Random(12345);
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer);

            parser.Process("\x1b[0m");

            for (int i = 0; i < 5000; i++)
            {
                string seq = GenerateRandomCsi(rnd);
                parser.Process(seq);
            }

            // Ensure reset still works
            parser.Process("\x1b[0m");

            var state = GetStyleState(buffer);
            Assert.False(state.Bold);
            Assert.False(state.Underline);
        }

        private static string GenerateRandomCsi(Random rnd)
        {
            const int MaxGeneratedCsiLength = 64;
            char[] leaders = { '\0', '?', '>', '<', '=' };
            string[] intermediates = { "", " ", "$", "/", "!" };
            char[] finals = { 'm', 'A', 'B', 'C', 'D', 'H', 'f', 'J', 'K', 'X', 'L', 'M', 'n', 'c', 'q', 'z', 'h', 'l' };

            char leader = leaders[rnd.Next(leaders.Length)];
            int parts = rnd.Next(0, 4);

            var sb = new StringBuilder();
            sb.Append("\x1b[");
            if (leader != '\0') sb.Append(leader);

            for (int i = 0; i < parts; i++)
            {
                int tokenKind = rnd.Next(6);
                switch (tokenKind)
                {
                    case 0:
                    case 1:
                    case 2:
                        sb.Append(rnd.Next(0, 1000));
                        break;
                    case 3:
                        sb.Append(';');
                        break;
                    case 4:
                        sb.Append(':');
                        break;
                    default:
                        sb.Append((char)('a' + rnd.Next(0, 26)));
                        break;
                }
            }

            sb.Append(intermediates[rnd.Next(intermediates.Length)]);
            sb.Append(finals[rnd.Next(finals.Length)]);
            string result = sb.ToString();
            if (result.Length <= MaxGeneratedCsiLength) return result;

            // Preserve ESC[ prefix and force a valid final byte while capping total length.
            char final = result[result.Length - 1];
            return result.Substring(0, MaxGeneratedCsiLength - 1) + final;
        }
    }
}
