using System;
using System.Text;
using NovaTerminal.Rendering;
using NovaTerminal.VT;
using SkiaSharp;
using Xunit;

namespace NovaTerminal.Rendering.Tests;

/// <summary>
/// Offline reproduction of terminal-browser's frame wire shape: mode 2026 sync wrapper,
/// cursor home, then a=T,t=f,i=1,p=1,C=1 with a base64 file path. Asserts the frame lands
/// in the visible-image snapshot a renderer would draw.
/// </summary>
public class TerminalBrowserStreamProbe
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

    [Fact]
    public void SyncWrappedCursorFrame_IsVisibleInSnapshot()
    {
        Assert.SkipUnless(SkiaAvailable, "SkiaSharp native library not available on this platform.");

        var buffer = new TerminalBuffer(86, 30);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: true)
        {
            ImageDecoder = new SkiaImageDecoder(),
            AllowNativeKittyGraphics = true,
            ReadFileBytes = _ => Rgba1376X960(),
            CellWidth = 11.333f,
            CellHeight = 24f,
        };

        string pathB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            @"C:\Users\behna\AppData\Local\Temp\terminal-browser-60056-0-0-0.rgba"));
        string frame = "\x1b[?2026h\x1b[H\x1b_Ga=T,f=32,s=1376,v=960,t=f,i=1,p=1,C=1,q=2;"
            + pathB64 + "\x1b\\\x1b[?2026l";

        // The live pane's actual prologue: alternate screen + terminal-browser's mode setup,
        // then 19 replacing frames (a static page redraws once, the stream then stops).
        parser.Process("[?1049h[?1003h[?1006h[?1016h[?2004h[?2048h[>1u[H");
        for (int i = 0; i < 19; i++)
        {
            parser.Process(frame);
        }

        buffer.Lock.EnterReadLock();
        try
        {
            var img = Assert.Single(buffer.Images);
            Assert.True(img.IsAltScreenImage == buffer.IsAltScreenActive, "screen-ownership tag mismatch");
            var visible = buffer.GetVisibleImagesSnapshot(0, buffer.Rows);
            Assert.Single(visible);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    private static byte[] Rgba1376X960()
    {
        var pixels = new byte[1376 * 960 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x30;
            pixels[i + 1] = 0x60;
            pixels[i + 2] = 0x90;
            pixels[i + 3] = 0xFF;
        }
        return pixels;
    }
}
