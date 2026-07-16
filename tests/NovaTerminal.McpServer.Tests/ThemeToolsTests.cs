using NovaTerminal.McpServer.Tools;

namespace NovaTerminal.McpServer.Tests;

public class ThemeToolsTests
{
    private const string ValidTheme = """
        {
          "Name": "Dracula",
          "Foreground": "#F8F8F2", "Background": "#282A36", "CursorColor": "#F8F8F2",
          "Black": "#21222C", "Red": "#FF5555", "Green": "#50FA7B", "Yellow": "#F1FA8C",
          "Blue": "#BD93F9", "Magenta": "#FF79C6", "Cyan": "#8BE9FD", "White": "#F8F8F2",
          "BrightBlack": "#6272A4", "BrightRed": "#FF6E6E", "BrightGreen": "#69FF94",
          "BrightYellow": "#FFFFA5", "BrightBlue": "#D6ACFF", "BrightMagenta": "#FF92DF",
          "BrightCyan": "#A4FFFF", "BrightWhite": "#FFFFFF"
        }
        """;

    [Fact]
    public void ValidTheme_PassesValidation()
    {
        var result = ThemeTools.ValidateThemeJson(ValidTheme);
        Assert.StartsWith("VALID", result);
        Assert.DoesNotContain("Errors:", result);
    }

    [Fact]
    public void MissingColorField_IsReported()
    {
        var missing = ValidTheme.Replace("\"BrightWhite\": \"#FFFFFF\"", "\"BrightWhite\": \"#FFFFFF\"".Replace("\"BrightWhite\"", "\"NotBrightWhite\""));
        var result = ThemeTools.ValidateThemeJson(missing);
        Assert.StartsWith("INVALID", result);
        Assert.Contains("Missing required color field 'BrightWhite'", result);
        // The renamed field is unknown -> reported as a warning, not fatal on its own.
        Assert.Contains("Unknown field 'NotBrightWhite'", result);
    }

    [Fact]
    public void InvalidColorValue_IsReported()
    {
        var bad = ValidTheme.Replace("\"Red\": \"#FF5555\"", "\"Red\": \"not-a-color\"");
        var result = ThemeTools.ValidateThemeJson(bad);
        Assert.StartsWith("INVALID", result);
        Assert.Contains("Field 'Red' has an invalid color value", result);
    }

    [Theory]
    [InlineData("#abc")]
    [InlineData("#AABBCC")]
    [InlineData("#aabbccdd")]
    [InlineData("sc#1,0.5,0.25")]
    [InlineData("sc#1,0.5,0.25,1")]
    public void AcceptedColorFormats_AreValid(string color)
    {
        var theme = ValidTheme.Replace("\"Red\": \"#FF5555\"", $"\"Red\": \"{color}\"");
        var result = ThemeTools.ValidateThemeJson(theme);
        Assert.StartsWith("VALID", result);
    }

    [Fact]
    public void NonJson_IsRejected()
    {
        Assert.StartsWith("INVALID", ThemeTools.ValidateThemeJson("not json {"));
    }

    [Fact]
    public void Empty_IsRejected()
    {
        Assert.StartsWith("INVALID", ThemeTools.ValidateThemeJson(""));
    }

    [Fact]
    public void NonObjectTopLevel_IsRejected()
    {
        Assert.StartsWith("INVALID", ThemeTools.ValidateThemeJson("[1,2,3]"));
    }

    [Fact]
    public void Schema_DescribesRequiredFields()
    {
        var schema = ThemeTools.GetThemeSchema();
        Assert.Contains("Name", schema);
        Assert.Contains("BrightWhite", schema);
        Assert.Contains("#RRGGBB", schema);
    }
}
