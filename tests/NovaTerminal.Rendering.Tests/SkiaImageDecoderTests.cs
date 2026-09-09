using System;
using NovaTerminal.Rendering;
using NovaTerminal.VT;
using SkiaSharp;
using Xunit;

namespace NovaTerminal.Rendering.Tests;

public class SkiaImageDecoderTests
{
    // Decoding renders through the SkiaSharp native library. Same convention as
    // SixelDecoderTests: present on Windows CI / dev machines, absent on the Linux
    // gating runner — skip there rather than fail.
    private static readonly bool SkiaAvailable = CheckSkiaAvailable();

    private static bool CheckSkiaAvailable()
    {
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Rgba8888));
            return surface != null;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void DecodeSixel_MinimalPayload_ReturnsBitmapWithPixelDimensions()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // One 6-pixel band, one column wide (same payload as SixelDecoderTests).
        var handle = new SkiaImageDecoder().DecodeSixel("0;0;0q#1;2;100;0;0#1~", out int width, out int height);

        var bitmap = Assert.IsType<SKBitmap>(handle);
        Assert.Equal(bitmap.Width, width);
        Assert.Equal(bitmap.Height, height);
        Assert.True(width > 0);
        Assert.True(height > 0);
    }

    [Theory]
    [InlineData("1;2;3")]   // no 'q' header terminator — anything below '?' is not sixel data
    [InlineData("0;0;0q")]  // header but no pixel data
    [InlineData("")]
    public void DecodeSixel_PayloadWithoutRenderableData_ReturnsNull(string dcs)
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var handle = new SkiaImageDecoder().DecodeSixel(dcs, out int width, out int height);

        Assert.Null(handle);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void DecodeImageBytes_PngPayload_ReturnsBitmapWithPixelDimensions()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        byte[] png;
        var source = new SKBitmap(3, 5);
        using (var image = SKImage.FromBitmap(source))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            png = data.ToArray();
        }

        var handle = new SkiaImageDecoder().DecodeImageBytes(png, out int width, out int height);

        var bitmap = Assert.IsType<SKBitmap>(handle);
        Assert.Equal(3, bitmap.Width);
        Assert.Equal(5, bitmap.Height);
        Assert.Equal(3, width);
        Assert.Equal(5, height);
    }

    [Fact]
    public void DecodeImageBytes_GarbageBytes_ReturnsNull()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var handle = new SkiaImageDecoder().DecodeImageBytes(new byte[] { 0x01, 0x02, 0x03 }, out int width, out int height);

        Assert.Null(handle);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    /// <summary>
    /// The dimension bound must fire from the container header BEFORE pixels are
    /// materialized: a compressed payload declaring huge dimensions would otherwise force
    /// the full decode allocation before any post-decode guard could reject it.
    /// </summary>
    [Fact]
    public void DecodeImageBytes_DimensionsOverBound_RejectedBeforeMaterialization()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        byte[] png;
        var source = new SKBitmap(3, 5);
        using (var image = SKImage.FromBitmap(source))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            png = data.ToArray();
        }

        var decoder = new SkiaImageDecoder { MaxPixelDimension = 1 };
        var handle = decoder.DecodeImageBytes(png, out int width, out int height);

        Assert.Null(handle);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void DecodeImageBytes_EmptyBytes_ReturnsNull()
    {
        var handle = new SkiaImageDecoder().DecodeImageBytes(Array.Empty<byte>(), out int width, out int height);

        Assert.Null(handle);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    /// <summary>
    /// End-to-end through the real parser: with the production decoder wired, a DCS sixel
    /// sequence must place an image in the buffer sized from the decoded bitmap's pixels
    /// (parser fallback cell metrics are 10x20 px). Without a decoder this path silently
    /// no-ops — the exact defect this wiring fixes.
    /// </summary>
    [Fact]
    public void AnsiParser_WithWiredDecoder_PlacesSixelImageInBuffer()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer) { ImageDecoder = new SkiaImageDecoder() };

        parser.Process("\x1bP0;0;0q#1;2;100;0;0#1~\x1b\\");

        buffer.Lock.EnterReadLock();
        try
        {
            var image = Assert.Single(buffer.Images);
            Assert.IsType<SKBitmap>(image.ImageHandle);
            Assert.Equal(1, image.CellWidth);  // ceil(1 px / 10 px fallback cell width)
            Assert.Equal(1, image.CellHeight); // ceil(6 px / 20 px fallback cell height)
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void DecodeRawImage_RgbaOddWidth_ProducesCorrectPixels()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        // Odd width on purpose: a stride/row-length bug would smear pixel (1,1).
        var pixels = new byte[3 * 2 * 4];
        pixels[0] = 0xFF; pixels[1] = 0x00; pixels[2] = 0x00; pixels[3] = 0xFF; // (0,0) red
        pixels[16] = 0x00; pixels[17] = 0x00; pixels[18] = 0xFF; pixels[19] = 0xFF; // (1,1) blue: (1*3+1)*4 = 16

        var handle = new SkiaImageDecoder().DecodeRawImage(pixels, bytesPerPixel: 4, width: 3, height: 2, out int width, out int height);

        var bitmap = Assert.IsType<SKBitmap>(handle);
        Assert.Equal(3, width);
        Assert.Equal(2, height);
        SKColor red = bitmap.GetPixel(0, 0);
        Assert.Equal(0xFF, red.Red);
        SKColor blue = bitmap.GetPixel(1, 1);
        Assert.Equal(0xFF, blue.Blue);
    }

    [Fact]
    public void DecodeRawImage_RgbThreeBytesPerPixel_ProducesOpaqueBitmap()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var pixels = new byte[] { 0x10, 0x20, 0x30 };

        var handle = new SkiaImageDecoder().DecodeRawImage(pixels, bytesPerPixel: 3, width: 1, height: 1, out _, out _);

        var bitmap = Assert.IsType<SKBitmap>(handle);
        SKColor pixel = bitmap.GetPixel(0, 0);
        Assert.Equal(0x10, pixel.Red);
        Assert.Equal(0x20, pixel.Green);
        Assert.Equal(0x30, pixel.Blue);
        Assert.Equal(0xFF, pixel.Alpha);
    }

    [Theory]
    [InlineData(4)]  // wrong pixel count
    [InlineData(3)]  // 3bpp declared 4 wide 2 high needs 24, not 12
    public void DecodeRawImage_LengthMismatch_ReturnsNull(int bytesPerPixel)
    {
        var decoder = new SkiaImageDecoder();
        var handle = decoder.DecodeRawImage(new byte[12], bytesPerPixel, width: 4, height: 2, out _, out _);
        Assert.Null(handle);
    }

    [Fact]
    public void DecodeRawImage_DimensionOverCap_ReturnsNull()
    {
        var decoder = new SkiaImageDecoder { MaxPixelDimension = 8 };
        var handle = decoder.DecodeRawImage(new byte[3 * 4], bytesPerPixel: 3, width: 16, height: 1, out _, out _);
        Assert.Null(handle);
    }
}

