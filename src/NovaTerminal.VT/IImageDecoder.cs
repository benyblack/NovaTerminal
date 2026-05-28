namespace NovaTerminal.VT
{
    public interface IImageDecoder
    {
        object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight);
        object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight);
    }
}
