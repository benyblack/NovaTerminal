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

    [Fact]
    public void KittyNativeApcUnderConPty_PlacesDecodedBitmapWhenAllowed()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true)
        {
            ImageDecoder = new SkiaImageDecoder(),
            AllowNativeKittyGraphics = true,
        };

        string base64 = Convert.ToBase64String(EncodePng3x5());
        parser.Process("\x1b_Gf=100,a=T,m=0;" + base64 + "\x1b\\");

        buffer.Lock.EnterReadLock();
        try
        {
            var image = Assert.Single(buffer.Images);
            Assert.IsType<SKBitmap>(image.ImageHandle);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void KittyNativeApcUnderConPty_DefaultPolicy_StillSkipsImage()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        // AllowNativeKittyGraphics defaults to false: the historical M4.2 policy (probe -> ERR,
        // images skipped) must remain the default for hosts that never opt in.
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true) { ImageDecoder = new SkiaImageDecoder() };
        Assert.False(parser.AllowNativeKittyGraphics);

        string base64 = Convert.ToBase64String(EncodePng3x5());
        parser.Process("\x1b_Gf=100,a=T,m=0;" + base64 + "\x1b\\");

        Assert.Empty(buffer.Images);
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new System.IO.MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] BuildRawRgba(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 0xFF;
        }
        return pixels;
    }

    /// <summary>
    /// The byte-exact inline frame shape zenbu-labs/terminal-browser's kitty.rs emits:
    /// a=T,f=32,o=z (raw RGBA, zlib), s/v pixel dims, U=1/c=/r= cell placement, q=2 quiet,
    /// base64-chunked via m=. Streamed under forced ConPTY filtering with native graphics
    /// allowed - the configuration the Windows port needs.
    /// </summary>
    [Fact]
    public void TerminalBrowserInlineFrame_ZlibRawRgba_PlacesDecodedBitmapInBuffer()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true)
        {
            ImageDecoder = new SkiaImageDecoder(),
            AllowNativeKittyGraphics = true,
        };

        byte[] rgba = BuildRawRgba(4, 2, 0xE8, 0x50, 0x2A);
        byte[] compressed = ZlibCompress(rgba);
        string base64 = Convert.ToBase64String(compressed);
        string firstChunk = base64.Substring(0, base64.Length / 2);
        string secondChunk = base64.Substring(base64.Length / 2);

        parser.Process("\x1b_Ga=T,f=32,o=z,s=4,v=2,t=d,i=7,U=1,c=4,r=2,q=2,m=1;" + firstChunk + "\x1b\\");
        Assert.Empty(buffer.Images); // m=1: still accumulating

        parser.Process("\x1b_Ga=T,f=32,o=z,s=4,v=2,t=d,i=7,U=1,c=4,r=2,q=2,m=0;" + secondChunk + "\x1b\\");

        buffer.Lock.EnterReadLock();
        try
        {
            var image = Assert.Single(buffer.Images);
            var bitmap = Assert.IsType<SKBitmap>(image.ImageHandle);
            Assert.Equal(4, bitmap.Width);
            Assert.Equal(2, bitmap.Height);
            // U=1,c=4,r=2 placement is explicit cells.
            Assert.Equal(4, image.CellWidth);
            Assert.Equal(2, image.CellHeight);
            // Swizzle sanity: first pixel decodes back to the source RGB.
            SKColor pixel = bitmap.GetPixel(0, 0);
            Assert.Equal(0xE8, pixel.Red);
            Assert.Equal(0x50, pixel.Green);
            Assert.Equal(0x2A, pixel.Blue);
            Assert.Equal(0xFF, pixel.Alpha);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void KittyFileTransport_RawRgba_PlacesDecodedBitmapViaInjectedReader()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        string? requestedPath = null;
        byte[] rgba = BuildRawRgba(4, 2, 0x10, 0x20, 0x30);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true)
        {
            ImageDecoder = new SkiaImageDecoder(),
            AllowNativeKittyGraphics = true,
            ReadFileBytes = path => { requestedPath = path; return rgba; },
        };

        // t=f: the payload is base64 of the file path, not of the pixels.
        string pathBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"C:\temp\terminal-browser-1.rgba"));
        parser.Process("\x1b_Ga=T,f=32,s=4,v=2,t=f,i=7,U=1,c=4,r=2,q=2,m=0;" + pathBase64 + "\x1b\\");

        Assert.Equal(@"C:\temp\terminal-browser-1.rgba", requestedPath);
        buffer.Lock.EnterReadLock();
        try
        {
            var image = Assert.Single(buffer.Images);
            var bitmap = Assert.IsType<SKBitmap>(image.ImageHandle);
            Assert.Equal(4, bitmap.Width);
            Assert.Equal(2, bitmap.Height);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void KittyFileTransport_WithoutReader_IsSkippedWithoutCrash()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true)
        {
            ImageDecoder = new SkiaImageDecoder(),
            AllowNativeKittyGraphics = true,
            // ReadFileBytes left null: the wired-app case is TerminalPane; a bare parser must
            // degrade to a logged skip, not throw.
        };

        string pathBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"C:\temp\frame.rgba"));
        parser.Process("\x1b_Ga=T,f=32,s=4,v=2,t=f,i=7,U=1,q=2,m=0;" + pathBase64 + "\x1b\\");

        Assert.Empty(buffer.Images);
    }

    [Fact]
    public void KittyShmTransport_IsSkippedWithoutCrash()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true)
        {
            ImageDecoder = new SkiaImageDecoder(),
            AllowNativeKittyGraphics = true,
        };

        string pathBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("/dev/shm/px-123-q"));
        parser.Process("\x1b_Ga=T,f=32,s=4,v=2,t=s,i=7,U=1,q=2,m=0;" + pathBase64 + "\x1b\\");

        Assert.Empty(buffer.Images);
    }

    /// <summary>
    /// terminal-browser streams a frame every ~33 ms as a=T,t=f,i=1,p=1,C=1: the same image
    /// number replaces the previous frame (AddKittyFrame) and C=1 keeps the cursor parked so
    /// the buffer never scrolls the fresh frame off the top.
    /// </summary>
    [Fact]
    public void KittyFrameStream_SameImageId_ReplacesAndKeepsCursor()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true)
        {
            ImageDecoder = new SkiaImageDecoder(),
            AllowNativeKittyGraphics = true,
        };

        string base64 = Convert.ToBase64String(EncodePng3x5());
        int beforeRow = buffer.CursorRow;
        int beforeCol = buffer.CursorCol;

        parser.Process("_Ga=T,t=d,f=100,i=1,p=1,C=1,q=2,m=0;" + base64 + "\\");
        parser.Process("_Ga=T,t=d,f=100,i=1,p=1,C=1,q=2,m=0;" + base64 + "\\");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Single(buffer.Images); // same i= replaced, not stacked
            Assert.Equal(beforeRow, buffer.CursorRow);
            Assert.Equal(beforeCol, buffer.CursorCol);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void KittyPlacement_WithoutCFlag_AdvancesCursorOverImageCells()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true)
        {
            ImageDecoder = new SkiaImageDecoder(),
            AllowNativeKittyGraphics = true,
        };

        string base64 = Convert.ToBase64String(EncodePng3x5());
        parser.Process("_Ga=T,t=d,f=100,i=2,c=4,r=2,m=0;" + base64 + "\\");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Single(buffer.Images);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
        // c=4,r=2 writes two reserved rows, then FinishImagePlacement's CR+LF lands the
        // cursor on the row below the image, at column 0 (#407/#413).
        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
    }
}
