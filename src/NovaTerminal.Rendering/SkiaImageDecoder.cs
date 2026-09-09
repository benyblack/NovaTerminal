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

        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;

            if (data == null || data.Length == 0)
            {
                return null;
            }

            if (bytesPerPixel != 3 && bytesPerPixel != 4)
            {
                return null;
            }

            if (width <= 0 || height <= 0 || width > MaxPixelDimension || height > MaxPixelDimension)
            {
                return null;
            }

            long expected = (long)width * height * bytesPerPixel;
            if (data.LongLength != expected)
            {
                return null;
            }

            // Raw kitty payloads are top-down RGB/RGBA; the surface we hand the renderer is
            // BGRA8888. Build the swizzled buffer separately so a malformed length can never
            // leave a partially-written bitmap behind.
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            byte[] bgra = new byte[checked(width * height * 4)];
            try
            {
                int src = 0;
                for (int dst = 0; dst < bgra.Length; dst += 4)
                {
                    bgra[dst] = data[src + 2];     // B
                    bgra[dst + 1] = data[src + 1]; // G
                    bgra[dst + 2] = data[src];     // R
                    bgra[dst + 3] = bytesPerPixel == 4 ? data[src + 3] : (byte)0xFF;
                    src += bytesPerPixel;
                }

                System.Runtime.InteropServices.Marshal.Copy(
                    bgra, 0, bitmap.GetPixels(), bgra.Length);
            }
            catch (Exception)
            {
                bitmap.Dispose();
                return null;
            }

            pixelWidth = width;
            pixelHeight = height;
            return bitmap;
        }
    }
}
