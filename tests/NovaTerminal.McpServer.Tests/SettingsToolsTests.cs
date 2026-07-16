using NovaTerminal.McpServer.Tools;

namespace NovaTerminal.McpServer.Tests;

public class SettingsToolsTests
{
    [Fact]
    public void Schema_DescribesAreasAndKeyFields()
    {
        var schema = SettingsTools.GetSettingsSchema();

        Assert.Contains("Appearance", schema);
        Assert.Contains("Behavior", schema);
        Assert.Contains("Command Assist", schema);
        Assert.Contains("Collections", schema);

        Assert.Contains("`FontSize`", schema);
        Assert.Contains("`WindowOpacity`", schema);
        Assert.Contains("`Profiles`", schema);
        Assert.Contains("`DefaultProfileId`", schema);
        Assert.Contains("PascalCase", schema);
        Assert.Contains("```json", schema);
    }

    private const string FullSettings = """
        {
          "FontSize": 14, "MaxHistory": 10000, "WindowOpacity": 1.0,
          "EnableLigatures": false, "WheelLinesPerNotch": 3.0,
          "BackgroundImageOpacity": 0.5, "BlurEffect": "Acrylic",
          "Keybindings": { "Ctrl+Shift+C": "copy" },
          "TabTemplateRules": [],
          "Profiles": [ { "Id": "00000000-0000-0000-0000-000000000001", "Name": "cmd" } ],
          "DefaultProfileId": "00000000-0000-0000-0000-000000000001"
        }
        """;

    [Fact]
    public void EmptyObject_IsValid()
    {
        var result = SettingsTools.ValidateSettingsJson("{}");
        Assert.StartsWith("VALID", result);
        Assert.DoesNotContain("Errors:", result);
    }

    [Fact]
    public void FullSettings_IsValid()
    {
        var result = SettingsTools.ValidateSettingsJson(FullSettings);
        Assert.StartsWith("VALID", result);
        Assert.DoesNotContain("Errors:", result);
    }

    [Fact]
    public void SchemaExample_PassesItsOwnValidator()
    {
        var schema = SettingsTools.GetSettingsSchema();
        int fence = schema.IndexOf("```json", StringComparison.Ordinal);
        Assert.True(fence >= 0, "schema must contain a ```json example");
        int bodyStart = schema.IndexOf('\n', fence) + 1;
        int bodyEnd = schema.IndexOf("```", bodyStart, StringComparison.Ordinal);
        string exampleJson = schema[bodyStart..bodyEnd];

        Assert.StartsWith("VALID", SettingsTools.ValidateSettingsJson(exampleJson));
    }

    [Fact]
    public void Empty_IsRejected() =>
        Assert.StartsWith("INVALID", SettingsTools.ValidateSettingsJson(""));

    [Fact]
    public void Null_IsRejected() =>
        Assert.StartsWith("INVALID", SettingsTools.ValidateSettingsJson(null!));

    [Fact]
    public void NonJson_IsRejected() =>
        Assert.StartsWith("INVALID", SettingsTools.ValidateSettingsJson("not json {"));

    [Fact]
    public void NonObjectRoot_IsRejected() =>
        Assert.StartsWith("INVALID", SettingsTools.ValidateSettingsJson("[1,2,3]"));

    [Fact]
    public void WrongBoolType_IsError()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "EnableLigatures": "yes" }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'EnableLigatures'", result);
    }

    [Fact]
    public void NonNumberFontSize_IsError()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "FontSize": "big" }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'FontSize'", result);
    }

    [Fact]
    public void FractionalIntField_IsError()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "MaxHistory": 1.5 }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'MaxHistory'", result);
    }

    [Fact]
    public void FontSizeZero_IsError()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "FontSize": 0 }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'FontSize'", result);
    }

    [Theory]
    [InlineData("WindowOpacity", "2")]
    [InlineData("BackgroundImageOpacity", "-1")]
    public void OpacityOutOfRange_IsError(string field, string value)
    {
        var result = SettingsTools.ValidateSettingsJson($$"""{ "{{field}}": {{value}} }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains($"'{field}'", result);
    }

    [Fact]
    public void NegativeMaxHistory_IsError()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "MaxHistory": -1 }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'MaxHistory'", result);
    }

    [Fact]
    public void MalformedDefaultProfileId_IsError()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "DefaultProfileId": "not-a-guid" }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'DefaultProfileId'", result);
    }

    [Fact]
    public void ProfilesNotArray_IsError()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "Profiles": {} }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'Profiles'", result);
    }

    [Fact]
    public void KeybindingsNotObject_IsError()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "Keybindings": [] }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'Keybindings'", result);
    }

    [Fact]
    public void KeybindingNonStringValue_IsError()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "Keybindings": { "Ctrl+C": 5 } }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("Keybindings", result);
    }

    [Fact]
    public void UnknownField_IsWarningButValid()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "Bogus": 1 }""");
        Assert.StartsWith("VALID", result);
        Assert.Contains("Unknown field 'Bogus'", result);
    }

    [Fact]
    public void WheelLinesPerNotchZero_IsWarning()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "WheelLinesPerNotch": 0 }""");
        Assert.StartsWith("VALID", result);
        Assert.Contains("WheelLinesPerNotch", result);
    }

    [Fact]
    public void PasswordField_IsSecurityWarning()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "Password": "secret" }""");
        Assert.StartsWith("VALID", result);
        Assert.Contains("Password", result);
        Assert.Contains("vault", result);
    }

    [Fact]
    public void EnumLikeStringArbitraryValue_IsAccepted()
    {
        var result = SettingsTools.ValidateSettingsJson("""{ "CursorStyle": "whatever" }""");
        Assert.StartsWith("VALID", result);
        Assert.DoesNotContain("CursorStyle", result);
    }
}
