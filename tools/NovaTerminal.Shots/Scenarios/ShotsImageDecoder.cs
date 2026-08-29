using NovaTerminal.Rendering;
using NovaTerminal.VT;
using SkiaSharp;

namespace NovaTerminal.Shots.Scenarios;

/// <summary>
/// The <see cref="IImageDecoder"/> a plain build never wires up (see
/// <see cref="InlineImageDecoding"/>'s remarks). Both methods do a real decode - nothing here
/// hands the parser a stand-in bitmap for a file it recognises by name.
/// </summary>
internal sealed class ShotsImageDecoder : IImageDecoder
{
    /// <summary>
    /// iTerm2's OSC 1337 payload: arbitrary image bytes (PNG, in both current scenarios), decoded
    /// with Skia's own image codec - the same one that already renders every other bitmap this
    /// harness produces.
    /// </summary>
    public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight)
    {
        SKBitmap? bitmap = SKBitmap.Decode(imageData);
        pixelWidth = bitmap?.Width ?? 0;
        pixelHeight = bitmap?.Height ?? 0;
        return bitmap;
    }

    /// <summary>
    /// DCS sixel data, decoded by <see cref="NovaTerminal.Rendering.SixelDecoder"/> - shipped
    /// production code that is otherwise never instantiated anywhere in <c>src/</c>.
    /// </summary>
    public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight)
    {
        SKBitmap? bitmap = new SixelDecoder().Decode(sixelData);
        pixelWidth = bitmap?.Width ?? 0;
        pixelHeight = bitmap?.Height ?? 0;
        return bitmap;
    }
}
