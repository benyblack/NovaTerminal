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

    private const string ValidProfile = """
        { "Name": "box", "Host": "box.internal", "Port": 22 }
        """;

    private const string ValidDocument = """
        {
          "SchemaVersion": 1,
          "Profiles": [
            {
              "Name": "prod-db", "Host": "prod.internal", "User": "svc", "Port": 2222,
              "BackendKind": 0, "AuthMode": 2, "IdentityFilePath": "/k/id_ed25519",
              "Tags": ["prod"], "RememberPasswordInVault": false,
              "JumpHops": [ { "Host": "j1", "User": "ops", "Port": 22 } ],
              "Forwards": [ { "Kind": 0, "BindAddress": "127.0.0.1", "SourcePort": 5432, "DestinationHost": "db", "DestinationPort": 5432 } ],
              "MuxOptions": { "Enabled": false, "ControlMasterAuto": true, "ControlPath": "", "ControlPersistSeconds": 0 },
              "RemoteShellKind": 1
            }
          ]
        }
        """;

    [Fact]
    public void MinimalProfile_IsValid()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson(ValidProfile);
        Assert.StartsWith("VALID", result);
        Assert.DoesNotContain("Errors:", result);
    }

    [Fact]
    public void FullDocument_IsValid()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson(ValidDocument);
        Assert.StartsWith("VALID", result);
        Assert.DoesNotContain("Errors:", result);
    }

    [Fact]
    public void SchemaExample_PassesItsOwnValidator()
    {
        var schema = ConnectionProfileTools.GetConnectionProfileSchema();
        int fence = schema.IndexOf("```json", StringComparison.Ordinal);
        Assert.True(fence >= 0, "schema must contain a ```json example");
        int bodyStart = schema.IndexOf('\n', fence) + 1;
        int bodyEnd = schema.IndexOf("```", bodyStart, StringComparison.Ordinal);
        string exampleJson = schema[bodyStart..bodyEnd];

        var result = ConnectionProfileTools.ValidateConnectionProfileJson(exampleJson);
        Assert.StartsWith("VALID", result);
    }

    [Fact]
    public void Empty_IsRejected() =>
        Assert.StartsWith("INVALID", ConnectionProfileTools.ValidateConnectionProfileJson(""));

    [Fact]
    public void NonJson_IsRejected() =>
        Assert.StartsWith("INVALID", ConnectionProfileTools.ValidateConnectionProfileJson("not json {"));

    [Fact]
    public void NonObjectRoot_IsRejected() =>
        Assert.StartsWith("INVALID", ConnectionProfileTools.ValidateConnectionProfileJson("[1,2,3]"));

    [Fact]
    public void MissingName_IsError()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Host": "h" }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("Missing required field 'Name'", result);
    }

    [Fact]
    public void MissingHost_IsError()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Name": "n" }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("Missing required field 'Host'", result);
    }

    [Fact]
    public void EnumOutOfRange_IsError()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Name": "n", "Host": "h", "AuthMode": 9 }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'AuthMode'", result);
        Assert.Contains("0=Default", result); // mapping hint
    }

    [Fact]
    public void EnumNotInteger_IsError()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Name": "n", "Host": "h", "BackendKind": "OpenSsh" }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'BackendKind'", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void PortOutOfRange_IsError(int port)
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson($$"""{ "Name": "n", "Host": "h", "Port": {{port}} }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'Port'", result);
    }

    [Fact]
    public void WrongType_IsError()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Name": "n", "Host": "h", "Port": "22" }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'Port'", result);
    }

    [Fact]
    public void ServerAliveCountMaxZero_IsError()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Name": "n", "Host": "h", "ServerAliveCountMax": 0 }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'ServerAliveCountMax'", result);
    }

    [Fact]
    public void ProfilesNotArray_IsError()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "SchemaVersion": 1, "Profiles": 5 }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'Profiles'", result);
    }

    [Fact]
    public void UnknownField_IsWarningButValid()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Name": "n", "Host": "h", "Bogus": 1 }""");
        Assert.StartsWith("VALID", result);
        Assert.Contains("Warnings:", result);
        Assert.Contains("Unknown field 'Bogus'", result);
    }

    [Fact]
    public void PasswordField_IsSecurityWarning()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Name": "n", "Host": "h", "Password": "secret" }""");
        Assert.StartsWith("VALID", result);
        Assert.Contains("Password", result);
        Assert.Contains("vault", result);
    }

    [Fact]
    public void MalformedId_IsError()
    {
        // A non-GUID Id makes the store quarantine the whole profiles.json on load, so it's fatal.
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Name": "n", "Host": "h", "Id": "not-a-guid" }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("'Id'", result);
    }

    [Fact]
    public void IdentityFileModeWithBlankPath_IsWarning()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Name": "n", "Host": "h", "AuthMode": 2, "IdentityFilePath": "" }""");
        Assert.StartsWith("VALID", result);
        Assert.Contains("IdentityFilePath", result);
    }

    [Fact]
    public void MissingSchemaVersionInDocument_IsWarning()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Profiles": [ { "Name": "n", "Host": "h" } ] }""");
        Assert.StartsWith("VALID", result);
        Assert.Contains("SchemaVersion", result);
    }

    [Fact]
    public void NewerSchemaVersion_IsWarning()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "SchemaVersion": 2, "Profiles": [ { "Name": "n", "Host": "h" } ] }""");
        Assert.StartsWith("VALID", result);
        Assert.Contains("SchemaVersion", result);
    }

    [Fact]
    public void Null_IsRejected() =>
        Assert.StartsWith("INVALID", ConnectionProfileTools.ValidateConnectionProfileJson(null!));

    [Fact]
    public void AutoDetect_SingleVsDocument_UsesCorrectPaths()
    {
        var single = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Host": "h" }""");
        Assert.Contains("Missing required field 'Name'", single);
        Assert.DoesNotContain("Profiles[", single);

        var doc = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Profiles": [ { "Host": "h" } ] }""");
        Assert.Contains("Profiles[0].Missing required field 'Name'", doc);
    }

    [Fact]
    public void JumpHopMissingHost_IsError()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson(
            """{ "Name": "n", "Host": "h", "JumpHops": [ { "User": "ops" } ] }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("JumpHops[0].Missing required field 'Host'", result);
    }

    [Fact]
    public void ForwardWithZeroSourcePort_IsError()
    {
        // The OpenSSH config compiler drops forwards with SourcePort <= 0, so zero is fatal.
        var result = ConnectionProfileTools.ValidateConnectionProfileJson(
            """{ "Name": "n", "Host": "h", "Forwards": [ { "Kind": 0, "SourcePort": 0, "DestinationHost": "db", "DestinationPort": 5432 } ] }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("Forwards[0].Field 'SourcePort'", result);
    }

    [Fact]
    public void LocalForwardMissingDestination_IsError()
    {
        // Omitted Kind deserializes to Local (0); Local/Remote forwards need a destination host + port.
        var result = ConnectionProfileTools.ValidateConnectionProfileJson(
            """{ "Name": "n", "Host": "h", "Forwards": [ { "SourcePort": 5432 } ] }""");
        Assert.StartsWith("INVALID", result);
        Assert.Contains("Forwards[0].Missing required field 'DestinationHost'", result);
        Assert.Contains("Forwards[0].Missing required field 'DestinationPort'", result);
    }

    [Fact]
    public void DynamicForwardWithSourcePortOnly_IsValid()
    {
        // Dynamic (SOCKS) forwards have no fixed destination, so only SourcePort is required.
        var result = ConnectionProfileTools.ValidateConnectionProfileJson(
            """{ "Name": "n", "Host": "h", "Forwards": [ { "Kind": 2, "SourcePort": 1080 } ] }""");
        Assert.StartsWith("VALID", result);
        Assert.DoesNotContain("Errors:", result);
    }
}
