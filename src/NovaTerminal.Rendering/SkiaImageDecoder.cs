using System;
using System.IO;
using NovaTerminal.VT;
using SkiaSharp;

namespace NovaTerminal.Rendering
{
    /// <summary>
    /// Decodes inline image payloads (DCS sixel bodies, Kitty/iTerm2 image bytes) into the
    /// bitmap handles the terminal draws. The draw operation pattern-matches the handle as
    /// <see cref="SKBitmap"/> exactly, so an <see cref="IImageDecoder"/> implementation is
    /// only reachable to the renderer when it produces that type.
    /// </summary>
    public sealed class SkiaImageDecoder : IImageDecoder
    {
        /// <summary>
        /// Largest declared width/height (in pixels) this decoder will materialize, applied
        /// from the container header BEFORE any pixels are decoded. Mirrors AnsiParser's
        /// post-decode guard: without a pre-decode bound, a small compressed payload can
        /// declare enormous dimensions and force the full allocation during decode, before
        /// the parser ever gets the chance to reject it.
        /// </summary>
        public int MaxPixelDimension { get; set; } = 2000;

        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;

            if (imageData == null || imageData.Length == 0)
            {
                return null;
            }

            // Sniffs the container (PNG/JPEG/WebP/...). Undecodable data must surface as a
            // decode failure (null), never an exception — the payload is remote-controlled
            // input handed over mid-parse.
            SKBitmap? bitmap;
            try
            {
                using var stream = new MemoryStream(imageData);
                using SKCodec? codec = SKCodec.Create(stream);
                if (codec == null)
                {
                    return null;
                }

                // Header check before materialization: rejecting here costs bytes, not the
                // width x height x 4 allocation the full decode would make.
                if (codec.Info.Width > MaxPixelDimension || codec.Info.Height > MaxPixelDimension)
                {
                    return null;
                }

                bitmap = SKBitmap.Decode(codec);
            }
            catch (Exception)
            {
                return null;
            }

            if (bitmap == null)
            {
                return null;
            }

            pixelWidth = bitmap.Width;
            pixelHeight = bitmap.Height;
            return bitmap;
        }

        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;

            if (string.IsNullOrEmpty(sixelData))
            {
                return null;
            }

            SKBitmap? bitmap = new SixelDecoder().Decode(sixelData);
            if (bitmap == null)
            {
                return null;
            }

            pixelWidth = bitmap.Width;
            pixelHeight = bitmap.Height;
            return bitmap;
        }
    }
}
