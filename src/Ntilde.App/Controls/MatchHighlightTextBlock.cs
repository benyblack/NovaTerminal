using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace NovaTerminal.Controls
{
    /// <summary>
    /// A single-line <see cref="TextBlock"/> that paints one run of its text in a second brush.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a control and not markup.</strong> The Command Assist rows need the matched part of
    /// the command emphasised, and a row is one line that must still ellipsize when the popup is
    /// narrower than the command. <c>Text="{Binding DisplayText}"</c> cannot colour part of a string,
    /// and the obvious alternative - three <c>TextBlock</c>s in a <c>StackPanel</c> - breaks the
    /// trimming: each segment would measure and ellipsize on its own, so a long command would be cut
    /// in the middle of the highlighted run with the tail still fully drawn beside it. One
    /// <c>TextBlock</c> with three <c>Run</c>s is laid out as one line and trims once, at the end,
    /// which is the behaviour the row already had.
    /// </para>
    /// <para>
    /// The span is supplied rather than searched for. Deciding <em>where</em> the match is belongs to
    /// the row view-model, which knows what the query was and can say so in a test that needs no
    /// rendering; this control only draws what it is told, including "nothing", which is what an empty
    /// or non-contiguous match produces.
    /// </para>
    /// </remarks>
    public class MatchHighlightTextBlock : TextBlock
    {
        /// <summary>The whole string to draw. Bound instead of <c>Text</c>, which this control owns.</summary>
        public static readonly StyledProperty<string?> SourceTextProperty =
            AvaloniaProperty.Register<MatchHighlightTextBlock, string?>(nameof(SourceText));

        /// <summary>Index of the run to emphasise, or a negative value for none.</summary>
        public static readonly StyledProperty<int> HighlightStartProperty =
            AvaloniaProperty.Register<MatchHighlightTextBlock, int>(nameof(HighlightStart), -1);

        /// <summary>Length of the run to emphasise. Zero means none.</summary>
        public static readonly StyledProperty<int> HighlightLengthProperty =
            AvaloniaProperty.Register<MatchHighlightTextBlock, int>(nameof(HighlightLength));

        /// <summary>Brush for the emphasised run. Null falls back to the control's own foreground.</summary>
        public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
            AvaloniaProperty.Register<MatchHighlightTextBlock, IBrush?>(nameof(HighlightBrush));

        public MatchHighlightTextBlock()
        {
            RebuildInlines();
        }

        public string? SourceText
        {
            get => GetValue(SourceTextProperty);
            set => SetValue(SourceTextProperty, value);
        }

        public int HighlightStart
        {
            get => GetValue(HighlightStartProperty);
            set => SetValue(HighlightStartProperty, value);
        }

        public int HighlightLength
        {
            get => GetValue(HighlightLengthProperty);
            set => SetValue(HighlightLengthProperty, value);
        }

        public IBrush? HighlightBrush
        {
            get => GetValue(HighlightBrushProperty);
            set => SetValue(HighlightBrushProperty, value);
        }

        /// <summary>
        /// Splits <paramref name="text"/> into the part before the match, the match, and the part
        /// after it - or returns <see langword="null"/> when there is no drawable match.
        /// </summary>
        /// <remarks>
        /// Static and Avalonia-free so every boundary case (empty span, span at either end, a span
        /// that runs off the end of a text that has since changed) is assertable without a render
        /// pass. The out-of-range case is not defensive noise: the two spans and the text arrive as
        /// three independent bindings, so there is a frame in which the new text is paired with the
        /// old offsets, and the honest answer for that frame is "no highlight" rather than a throw.
        /// </remarks>
        internal static (string Before, string Match, string After)? SplitForHighlight(string? text, int start, int length)
        {
            if (string.IsNullOrEmpty(text) || length <= 0 || start < 0 || start >= text!.Length)
            {
                return null;
            }

            if (start + length > text.Length)
            {
                return null;
            }

            return (text[..start], text.Substring(start, length), text[(start + length)..]);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SourceTextProperty ||
                change.Property == HighlightStartProperty ||
                change.Property == HighlightLengthProperty ||
                change.Property == HighlightBrushProperty)
            {
                RebuildInlines();
            }
        }

        /// <summary>
        /// Republishes the inline runs for the current text and span.
        /// </summary>
        /// <remarks>
        /// Empty segments are dropped rather than added as zero-length runs. A match at index 0 or at
        /// the very end therefore produces two runs, not three with a blank on one side: the visible
        /// result is identical and the run list stays something a test can read literally.
        /// </remarks>
        private void RebuildInlines()
        {
            string text = SourceText ?? string.Empty;
            InlineCollection? inlines = Inlines;

            if (inlines == null)
            {
                // No inline collection to write into (a bare, never-templated instance). The plain
                // text is still the right thing to draw; the highlight is the part that is optional.
                Text = text;
                return;
            }

            inlines.Clear();

            (string Before, string Match, string After)? split = SplitForHighlight(text, HighlightStart, HighlightLength);
            if (split == null)
            {
                if (text.Length > 0)
                {
                    inlines.Add(new Run(text));
                }

                return;
            }

            if (split.Value.Before.Length > 0)
            {
                inlines.Add(new Run(split.Value.Before));
            }

            var match = new Run(split.Value.Match);
            if (HighlightBrush != null)
            {
                match.Foreground = HighlightBrush;
            }

            inlines.Add(match);

            if (split.Value.After.Length > 0)
            {
                inlines.Add(new Run(split.Value.After));
            }
        }
    }
}
