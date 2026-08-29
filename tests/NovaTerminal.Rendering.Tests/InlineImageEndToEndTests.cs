using System;
using NovaTerminal.Rendering;
using NovaTerminal.VT;
using SkiaSharp;
using Xunit;

namespace NovaTerminal.Rendering.Tests;

/// <summary>
/// Decoder-wired end-to-end coverage for the inline image paths that reach
/// <see cref="IImageDecoder.DecodeImageBytes"/> (the DCS sixel e2e lives next to the decoder
/// tests in <see cref="SkiaImageDecoderTests"/>). Each test feeds the real wire encoding
/// through the real parser with the production decoder and asserts a placed
/// <see cref="TerminalImage"/> sized from the decoded bitmap — the assertions would fail
/// with the pre-#369 wiring, where every path silently no-op'd.
/// </summary>
public class InlineImageEndToEndTests
{
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

    private static byte[] EncodePng3x5()
    {
        var source = new SKBitmap(3, 5);
        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Osc1337_InlineImage_PlacesDecodedBitmapInBuffer()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer) { ImageDecoder = new SkiaImageDecoder() };
        string base64 = Convert.ToBase64String(EncodePng3x5());

        parser.Process("\x1b]1337;File=name=t.png;inline=1:" + base64 + "\x07");

        buffer.Lock.EnterReadLock();
        try
        {
            var image = Assert.Single(buffer.Images);
            Assert.IsType<SKBitmap>(image.ImageHandle);
            // Auto-fit with fallback 10x20 cell metrics: width = max(10, ceil(3/10)) = 10,
            // height = round(10 * (10/20) * (5/3)) = 8.
            Assert.Equal(10, image.CellWidth);
            Assert.Equal(8, image.CellHeight);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void KittyChunkedApc_PlacesDecodedBitmapInBuffer()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        // Non-tunneled Kitty APC is skipped when ConPTY filtering is likely (the Windows
        // default); force it off to exercise the direct path Linux/macOS take.
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = new SkiaImageDecoder() };

        string base64 = Convert.ToBase64String(EncodePng3x5());
        string firstChunk = base64.Substring(0, base64.Length / 2);
        string secondChunk = base64.Substring(base64.Length / 2);

        parser.Process("\x1b_Gf=100,m=1;" + firstChunk + "\x1b\\");
        Assert.Empty(buffer.Images); // m=1: accumulating, nothing finalized yet

        parser.Process("\x1b_Gm=0;" + secondChunk + "\x1b\\");

        buffer.Lock.EnterReadLock();
        try
        {
            var image = Assert.Single(buffer.Images);
            Assert.IsType<SKBitmap>(image.ImageHandle);
            // Auto-fit with fallback 10x20 cell metrics: width = ceil(3/10) = 1,
            // height = ceil(1 * (10/20) * (5/3)) = 1.
            Assert.Equal(1, image.CellWidth);
            Assert.Equal(1, image.CellHeight);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void KittyTunneledOverOsc1339_PlacesDecodedBitmapDespiteConPtyFiltering()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        // The tunnel exists precisely because ConPTY strips DCS/APC: with filtering forced
        // on, the direct APC path is skipped but the OSC 1339 tunnel must still decode.
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true) { ImageDecoder = new SkiaImageDecoder() };

        string base64 = Convert.ToBase64String(EncodePng3x5());
        parser.Process("\x1b]1339;Kitty:Gf=100,a=t,m=0;" + base64 + "\x07");

        buffer.Lock.EnterReadLock();
        try
        {
            var image = Assert.Single(buffer.Images);
            Assert.IsType<SKBitmap>(image.ImageHandle);
            Assert.Equal(1, image.CellWidth);
            Assert.Equal(1, image.CellHeight);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }
}
