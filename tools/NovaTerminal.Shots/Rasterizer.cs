using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace NovaTerminal.Shots;

public static class Rasterizer
{
    /// <summary>
    /// Rasterizes the whole window, chrome included. CaptureRenderedFrame is used rather than
    /// RenderTargetBitmap because the window's own title bar, tab strip, and every in-window
    /// overlay must appear; MainWindow sets ExtendClientAreaToDecorationsHint, so this frame
    /// contains essentially the entire visual identity.
    /// </summary>
    public static SKBitmap CaptureWindow(Window window, double scale)
    {
        window.SetRenderScaling(scale);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        using WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException(
                "CaptureRenderedFrame returned null. The window is not rendering — check that " +
                "ShotsAppBuilder still sets UseHeadlessDrawing = false.");

        using var stream = new MemoryStream();
        frame.Save(stream);
        stream.Position = 0;

        return SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException("Could not decode the captured frame.");
    }

    public static void WritePng(SKBitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>
    /// Share of pixels differing from the image's most common colour. A capture that comes back
    /// near zero is a blank raster, which is the failure mode that looks like success.
    /// </summary>
    public static double InkFraction(SKBitmap bitmap)
    {
        if (bitmap.Width == 0 || bitmap.Height == 0)
        {
            throw new ArgumentException(
                $"Bitmap has no pixels ({bitmap.Width}x{bitmap.Height}). InkFraction cannot tell a " +
                "degenerate capture from a detected-blank one, so it refuses rather than returning 0.0.",
                nameof(bitmap));
        }

        var counts = new Dictionary<uint, int>();

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                uint key = (uint)bitmap.GetPixel(x, y);
                counts[key] = counts.TryGetValue(key, out int existing) ? existing + 1 : 1;
            }
        }

        int total = bitmap.Width * bitmap.Height;
        int dominant = counts.Values.Max();

        return (double)(total - dominant) / total;
    }
}
