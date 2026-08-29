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
}
