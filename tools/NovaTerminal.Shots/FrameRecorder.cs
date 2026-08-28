using Avalonia.Controls;
using SkiaSharp;

namespace NovaTerminal.Shots;

/// <summary>Writes a numbered PNG per captured frame for ffmpeg to consume.</summary>
public sealed class FrameRecorder
{
    private readonly Window _window;
    private readonly string _frameDirectory;
    private readonly double _scale;

    public FrameRecorder(Window window, string frameDirectory, double scale)
    {
        _window = window;
        _frameDirectory = frameDirectory;
        _scale = scale;
        Directory.CreateDirectory(frameDirectory);
    }

    public int FrameCount { get; private set; }

    public void CaptureFrame()
    {
        using SKBitmap bitmap = Rasterizer.CaptureWindow(_window, _scale);
        Rasterizer.WritePng(bitmap, Path.Combine(_frameDirectory, $"frame-{FrameCount:D5}.png"));
        FrameCount++;
    }
}
