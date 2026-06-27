using NovaTerminal.McpServer.Tools;

namespace NovaTerminal.McpServer.Tests;

public class ConnectionProfileToolsTests
{
    [Fact]
    public void Schema_DescribesFieldGroupsAndEnumMappings()
    {
        var schema = ConnectionProfileTools.GetConnectionProfileSchema();

        // Field-group headings.
        Assert.Contains("Identity", schema);
        Assert.Contains("Connection", schema);
        Assert.Contains("Auth", schema);
        Assert.Contains("Jump hosts", schema);
        Assert.Contains("Port forwarding", schema);
        Assert.Contains("Multiplexing", schema);
        Assert.Contains("Document wrapper", schema);

        // Integer enum mappings must be spelled out (enums are ints on the wire).
        Assert.Contains("0=OpenSsh", schema);
        Assert.Contains("1=Native", schema);
        Assert.Contains("2=IdentityFile", schema);
        Assert.Contains("2=Dynamic", schema);
        Assert.Contains("4=Pwsh", schema);

        // Required fields and an example are present.
        Assert.Contains("`Name`", schema);
        Assert.Contains("`Host`", schema);
        Assert.Contains("```json", schema);
    }
}
