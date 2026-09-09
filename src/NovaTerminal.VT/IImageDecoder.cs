namespace NovaTerminal.VT
{
    public interface IImageDecoder
    {
        object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight);
        object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight);

        /// <summary>
        /// Decodes raw pixel data (kitty graphics <c>f=24</c> RGB / <c>f=32</c> RGBA, e.g. the
        /// contents of a <c>t=f</c> transport file) into the same handle type the container
        /// decoder produces. <paramref name="data"/> must contain exactly
        /// <c>width * height * bytesPerPixel</c> bytes, top-down rows in order.
        /// Returns null on any dimension/length mismatch rather than throwing.
        /// </summary>
        object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight);
    }
}
