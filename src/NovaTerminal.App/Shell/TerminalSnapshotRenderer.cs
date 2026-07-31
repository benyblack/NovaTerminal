using System;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using NovaTerminal.Rendering;
using NovaTerminal.VT;
using SkiaSharp;

namespace NovaTerminal.Shell
{
    /// <summary>
    /// How a snapshot resolves the primary Skia font it renders with.
    /// </summary>
    public enum SnapshotFontResolution
    {
        /// <summary>
        /// Plain <see cref="SKTypeface.FromFamilyName(string)"/> lookup with Skia's
        /// default edging and hinting. This is the golden-baseline path: the
        /// render tests' PNG baselines were captured through it, so it must not
        /// change behaviour.
        /// </summary>
        Simple,

        /// <summary>
        /// The same resolution the live control uses on screen
        /// (<see cref="TerminalView.ResolveMonospacePrimaryTypeface"/>: bundled
        /// font catalog first, then the monospace fallbacks that are probed for
        /// box-drawing coverage, antialiased and hinted). Use this when the
        /// snapshot is meant to look like what the user is looking at.
        /// </summary>
        LiveParity,
    }

    /// <summary>
    /// A live pane's render inputs as plain values, so a snapshot can reproduce
    /// what is on screen from another thread without reading the control.
    /// Published by the pane on the UI thread (see
    /// <c>TerminalPane.UpdateAgentRenderParameters</c>).
    /// </summary>
    /// <param name="Metrics">Cell geometry measured by the live control.</param>
    /// <param name="FontFamily">
    /// The configured family name — the same string the control resolves its
    /// primary typeface from, so <see cref="SnapshotFontResolution.LiveParity"/>
    /// lands on the same font.
    /// </param>
    public readonly record struct PaneRenderParameters(
        CellMetrics Metrics,
        string FontFamily,
        float FontSize,
        bool EnableLigatures,
        bool EnableComplexShaping)
    {
        /// <summary>
        /// False before the control has measured its font (a pane that was just
        /// created, or was never laid out): there is no geometry to render into.
        /// </summary>
        public bool IsUsable =>
            Metrics.CellWidth > 0 &&
            Metrics.CellHeight > 0 &&
            FontSize > 0 &&
            !string.IsNullOrWhiteSpace(FontFamily);
    }

    /// <summary>Knobs for <see cref="TerminalSnapshotRenderer.Capture"/>.</summary>
    public sealed class TerminalSnapshotOptions
    {
        /// <summary>Selection to paint, or null for "nothing selected".</summary>
        public SelectionState? Selection { get; init; }

        public bool HideCursor { get; init; }
        public bool EnableLigatures { get; init; }
        public bool EnableComplexShaping { get; init; } = true;

        /// <summary>
        /// DPI factor the draw operation snaps to. Defaults to 1.0 — one device
        /// pixel per DIP — deliberately: inheriting the current monitor's scaling
        /// would make the same buffer render differently per machine, which no
        /// determinism test could pin down.
        /// </summary>
        public double RenderScaling { get; init; } = 1.0;

        public string TypefaceFamily { get; init; } = DefaultTypefaceFamily;
        public float FontSize { get; init; } = 14f;

        /// <summary>Alpha applied to the rendered content (the live control's window opacity).</summary>
        public double Opacity { get; init; } = 1.0;

        /// <summary>See <see cref="SnapshotFontResolution"/>.</summary>
        public SnapshotFontResolution FontResolution { get; init; } = SnapshotFontResolution.Simple;

        /// <summary>
        /// When true the buffer's theme background is painted across the canvas
        /// before the terminal is drawn.
        /// </summary>
        /// <remarks>
        /// The draw operation deliberately leaves default-background cells
        /// transparent (the live control fills the theme background underneath it,
        /// and <c>TransparentDefaultEqualBackgroundTests</c> pins that down). A
        /// snapshot that is going to be looked at as an image therefore has to
        /// fill it here, or every unstyled cell comes out transparent. Default
        /// false so existing golden baselines keep their transparency.
        /// </remarks>
        public bool FillBackground { get; init; }

        /// <summary>Row-picture cache to render with, or null to render uncached.</summary>
        /// <remarks>
        /// Leave null for any capture that must be deterministic or that runs off
        /// the render thread: the caches belong to the live control and are not
        /// safe to touch from another thread.
        /// </remarks>
        public RowImageCache? RowCache { get; init; }

        /// <summary>Glyph atlas to render with, or null to render uncached. See <see cref="RowCache"/>.</summary>
        public GlyphCache? GlyphCache { get; init; }

        /// <summary>Font family list used when none is supplied.</summary>
        public const string DefaultTypefaceFamily =
            "Cascadia Code PL, CaskaydiaCove Nerd Font, Cascadia Code, Consolas, Monospace";
    }

    /// <summary>
    /// Renders a terminal buffer to a bitmap through the real
    /// <see cref="TerminalDrawOperation"/>, with no window, visual tree, or GPU
    /// surface involved: the draw operation only needs an <see cref="SKCanvas"/>.
    ///
    /// This is the one capture path in the product. The render tests' golden PNGs,
    /// the agent-host <c>captureScreen</c> method, and any future CLI PNG output
    /// all go through it, so a snapshot an agent takes is produced by the same
    /// code the baselines pin down.
    ///
    /// Thread affinity: safe to call off the UI thread. It takes the buffer's read
    /// lock for the values it needs, releases it, and then draws (the draw
    /// operation re-enters the lock itself). It never touches the live control or
    /// its caches — pass no caches and it allocates its own Skia font objects.
    /// </summary>
    public static class TerminalSnapshotRenderer
    {
        /// <summary>
        /// Renders <paramref name="buffer"/> into a new <paramref name="width"/> x
        /// <paramref name="height"/> bitmap. The caller owns the bitmap.
        /// </summary>
        public static SKBitmap Capture(
            TerminalBuffer buffer,
            CellMetrics metrics,
            int width,
            int height,
            TerminalSnapshotOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            options ??= new TerminalSnapshotOptions();

            // Snapshot the geometry/cursor values under the read lock, then let go:
            // DrawTerminalInternal re-enters the lock, and the buffer's lock has no
            // recursion policy to fall back on.
            int snapshotRows, snapshotCols, totalLines, cursorRow, cursorCol;
            TerminalTheme theme;
            buffer.Lock.EnterReadLock();
            try
            {
                snapshotRows = buffer.Rows;
                snapshotCols = buffer.Cols;
                totalLines = buffer.InternalTotalLines;
                cursorRow = buffer.InternalCursorRow;
                cursorCol = buffer.InternalCursorCol;
                theme = buffer.Theme;
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            var bitmap = new SKBitmap(width, height);
            var canvas = new SKCanvas(bitmap);

            var typeface = new Typeface(options.TypefaceFamily);
            var glyphTypeface = typeface.GlyphTypeface;

            SKTypeface? primary = options.FontResolution == SnapshotFontResolution.LiveParity
                ? TerminalView.ResolveMonospacePrimaryTypeface(typeface.FontFamily.Name, out _)
                : SKTypeface.FromFamilyName(typeface.FontFamily.Name);

            // Deliberately not substituted when the lookup comes back empty: the
            // Simple path has to keep whatever Skia does with a missing family,
            // because that is what the golden baselines were captured against.
            var skTypeface = new SharedSKTypeface(primary!);
            var skFont = new SharedSKFont(new SKFont(skTypeface.Typeface!, options.FontSize));
            if (options.FontResolution == SnapshotFontResolution.LiveParity && skFont.Font != null)
            {
                skFont.Font.Edging = SKFontEdging.Antialias;
                skFont.Font.Hinting = SKFontHinting.Normal;
            }

            var fallbackChain = options.FontResolution == SnapshotFontResolution.LiveParity
                ? TerminalView.GetSnapshotFallbackChain()
                : Array.Empty<SKTypeface>();

            var op = new TerminalDrawOperation(
                new Rect(0, 0, width, height),
                buffer,
                scrollOffset: 0,
                selection: options.Selection ?? new SelectionState(),
                searchMatches: null,
                activeSearchIndex: -1,
                metrics: metrics,
                typeface: typeface,
                fontSize: options.FontSize,
                glyphTypeface: glyphTypeface,
                skTypeface: skTypeface,
                skFont: skFont,
                enableLigatures: options.EnableLigatures,
                fallbackCache: new ConcurrentDictionary<string, SKTypeface?>(),
                fallbackChain: fallbackChain,
                opacity: options.Opacity,
                hideCursor: options.HideCursor,
                renderScaling: options.RenderScaling <= 0 ? 1.0 : options.RenderScaling,
                snapshotRows: snapshotRows,
                snapshotCols: snapshotCols,
                totalLines: totalLines,
                cursorRow: cursorRow,
                cursorCol: cursorCol,
                rowCache: options.RowCache,
                enableComplexShaping: options.EnableComplexShaping,
                glyphCache: options.GlyphCache);

            try
            {
                if (options.FillBackground)
                {
                    var bg = theme.Background;
                    canvas.Clear(new SKColor(bg.R, bg.G, bg.B, bg.A));
                }

                op.DrawTerminalInternal(canvas);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
            finally
            {
                op.Dispose();
                skFont.Dispose();
                skTypeface.Dispose();
                canvas.Dispose();
            }
        }

        /// <summary>Renders and PNG-encodes in one step.</summary>
        public static byte[] CapturePng(
            TerminalBuffer buffer,
            CellMetrics metrics,
            int width,
            int height,
            TerminalSnapshotOptions? options = null)
        {
            using var bitmap = Capture(buffer, metrics, width, height, options);
            return EncodePng(bitmap);
        }

        public static byte[] EncodePng(SKBitmap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        /// <summary>
        /// Resamples <paramref name="source"/> down so its width is at most
        /// <paramref name="maxWidth"/>, preserving aspect ratio. Returns null when
        /// the source already fits (nothing to do) so callers can keep the original.
        /// </summary>
        public static SKBitmap? DownscaleToWidth(SKBitmap source, int maxWidth)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (maxWidth <= 0 || source.Width <= maxWidth) return null;

            // Height rounds up so a 1-pixel-tall result can never come out as 0.
            int height = Math.Max(1, (int)Math.Ceiling(source.Height * (double)maxWidth / source.Width));
            var resized = new SKBitmap(maxWidth, height);

            // Fixed sampling: the same input must always resample to the same bytes.
            if (!source.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)))
            {
                resized.Dispose();
                return null;
            }
            return resized;
        }
    }
}
