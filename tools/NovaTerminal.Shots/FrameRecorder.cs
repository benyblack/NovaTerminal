using Avalonia.Controls;
using SkiaSharp;

namespace NovaTerminal.Shots;

/// <summary>Writes a numbered PNG per captured frame for ffmpeg to consume.</summary>
public sealed class FrameRecorder
{
    private readonly Window _window;
    private readonly string _frameDirectory;
    private readonly double _scale;
    private (int Width, int Height)? _canvasSize;

    public FrameRecorder(Window window, string frameDirectory, double scale)
    {
        _window = window;
        _frameDirectory = frameDirectory;
        _scale = scale;
        Directory.CreateDirectory(frameDirectory);
    }

    public int FrameCount { get; private set; }

    public void CaptureFrame() => CaptureFrame(_window);

    /// <summary>
    /// Captures a window other than the one this recorder was constructed for, into the same
    /// numbered sequence.
    /// </summary>
    /// <remarks>
    /// A clip is one contiguous frame-*.png sequence regardless of which Avalonia Window produced
    /// each frame, so a scenario that cuts away to a modal dialog mid-recording - the agent
    /// activity journal, say - and back keeps a single FrameRecorder and a single incrementing
    /// FrameCount rather than starting a second recorder that would collide on frame-00000.png.
    /// </remarks>
    public void CaptureFrame(Window window)
    {
        using SKBitmap raw = Rasterizer.CaptureWindow(window, _scale);
        using SKBitmap toWrite = FitToCanvas(raw);
        Rasterizer.WritePng(toWrite, Path.Combine(_frameDirectory, $"frame-{FrameCount:D5}.png"));
        FrameCount++;
    }

    /// <summary>
    /// Redraws <paramref name="bitmap"/> onto a single fixed canvas size and pixel format for
    /// the whole recording, established by whichever window this recorder captures first. Never
    /// scales - a smaller source is centered and letterboxed instead.
    /// </summary>
    /// <remarks>
    /// ffmpeg's paletteuse filter - the GIF encode path's second pass - throws "Internal bug,
    /// should not have happened" the moment either the resolution *or* the pixel format changes
    /// mid-stream (confirmed by reproducing the failure directly against ffmpeg outside this
    /// harness). A clip that cuts from the main window to a smaller modal dialog and back changes
    /// the resolution on every cut; less obviously, it can also change the pixel format even
    /// when the size matches, because <see cref="Rasterizer.CaptureWindow"/> decodes each frame's
    /// PNG independently and SKBitmap.Decode does not promise the same SKColorType for every one
    /// - only for how each one happened to encode. So every frame is redrawn onto a freshly
    /// allocated bitmap with an explicit, fixed <see cref="SKColorType"/> - never just copied or
    /// passed through as-is - even one that already matches the canvas size. Centering the
    /// smaller frame rather than scaling it up keeps the journal's text pixel-sharp, which a
    /// scaled-up capture would blur.
    /// </remarks>
    private SKBitmap FitToCanvas(SKBitmap bitmap)
    {
        _canvasSize ??= (bitmap.Width, bitmap.Height);
        (int width, int height) = _canvasSize.Value;

        var canvasBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(bitmap, (width - bitmap.Width) / 2f, (height - bitmap.Height) / 2f);
        }

        return canvasBitmap;
    }
}
