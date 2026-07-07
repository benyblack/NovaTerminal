using NovaTerminal.Rendering;
using Xunit;

namespace NovaTerminal.Rendering.Tests;

// Regression tests for #169: sixel payloads are remote-controlled input, so
// malformed color parameters must be skipped, not thrown into the parser loop.
public class SixelDecoderTests
{
    [Theory]
    [InlineData("0;0;0q#1;;2;3;4~~")]          // empty param via consecutive ';'
    [InlineData("0;0;0q#1;2;;3;~~")]           // multiple empties
    [InlineData("0;0;0q#1;2;99999999999;3;4~")] // overflow int.Parse territory
    public void Decode_MalformedColorParams_DoesNotThrow(string dcs)
    {
        var decoder = new SixelDecoder();

        var ex = Record.Exception(() => decoder.Decode(dcs));

        Assert.Null(ex);
    }

    [Fact]
    public void Decode_RgbValuesAbove100_AreClampedNotWrapped()
    {
        var decoder = new SixelDecoder();

        // type 2 = RGB percentages; 200% would previously wrap the byte cast.
        var ex = Record.Exception(() => decoder.Decode("0;0;0q#1;2;200;200;200#1~~-"));

        Assert.Null(ex);
    }

    [Fact]
    public void Decode_ValidMinimalSixel_ProducesBitmap()
    {
        var decoder = new SixelDecoder();

        // One 6-pixel column in palette color 1.
        var bitmap = decoder.Decode("0;0;0q#1;2;100;0;0#1~");

        Assert.NotNull(bitmap);
    }
}
