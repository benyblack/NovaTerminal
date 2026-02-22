using System.Text.Json;
using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests
{
    public class ThemeSerializationTests
    {
        [Fact]
        public void SerializeTheme_TermColorsAreWrittenAsStrings()
        {
            var theme = new TerminalTheme
            {
                Name = "SerializeCheck",
                Foreground = TermColor.FromRgb(0x11, 0x22, 0x33),
                Background = TermColor.FromRgb(0x44, 0x55, 0x66),
                CursorColor = TermColor.FromRgb(0x77, 0x88, 0x99),
                Red = TermColor.FromRgb(0xAA, 0x00, 0x00)
            };

            string json = JsonSerializer.Serialize(theme, AppJsonContext.Default.TerminalTheme);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(JsonValueKind.String, root.GetProperty("Foreground").ValueKind);
            Assert.Equal("#112233", root.GetProperty("Foreground").GetString());
            Assert.Equal("#445566", root.GetProperty("Background").GetString());
            Assert.Equal("#778899", root.GetProperty("CursorColor").GetString());
            Assert.Equal("#AA0000", root.GetProperty("Red").GetString());
        }

        [Fact]
        public void DeserializeTheme_TermColorsReadFromStringAndObject()
        {
            const string json = """
            {
              "Name": "RoundTripCheck",
              "Foreground": "#010203",
              "Background": "Black",
              "CursorColor": { "R": 16, "G": 32, "B": 48, "A": 255 }
            }
            """;

            var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme);

            Assert.NotNull(theme);
            Assert.Equal(new TermColor(1, 2, 3), theme!.Foreground);
            Assert.Equal(TermColor.Black, theme.Background);
            Assert.Equal(new TermColor(16, 32, 48), theme.CursorColor);
        }
    }
}
