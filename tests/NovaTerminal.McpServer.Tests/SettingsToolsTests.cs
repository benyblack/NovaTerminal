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
}
