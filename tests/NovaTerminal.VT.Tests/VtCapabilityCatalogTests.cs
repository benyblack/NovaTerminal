using NovaTerminal.VtContract;

namespace NovaTerminal.VT.Tests;

public sealed class VtCapabilityCatalogTests
{
    [Fact]
    public void Parse_ReturnsTypedCapabilitiesInManifestOrder()
    {
        IReadOnlyList<VtCapability> capabilities = VtCapabilityCatalog.Parse(Manifest(
            Entry("CSI:E", "CNL", "supported", "cursor-next-line"),
            Entry("CSI:F", "CPL", "partial", null)));

        Assert.Collection(
            capabilities,
            capability =>
            {
                Assert.Equal("CSI:E", capability.Key);
                Assert.Equal("CNL", capability.Mnemonic);
                Assert.Equal(VtSupport.Supported, capability.Support);
                Assert.Equal("cursor-next-line", capability.ContractCase);
            },
            capability =>
            {
                Assert.Equal("CSI:F", capability.Key);
                Assert.Equal(VtSupport.Partial, capability.Support);
            });
    }

    [Fact]
    public void Parse_ReturnsAReadOnlyCatalog()
    {
        IReadOnlyList<VtCapability> capabilities = VtCapabilityCatalog.Parse(Manifest(
            Entry("CSI:E", "CNL", "supported", "cursor-next-line")));
        ICollection<VtCapability> collection = Assert.IsAssignableFrom<ICollection<VtCapability>>(capabilities);

        Assert.Throws<NotSupportedException>(() => collection.Add(capabilities[0]));
    }

    [Fact]
    public void Parse_RejectsDuplicateKeys()
    {
        string json = Manifest(
            Entry("CSI:E", "CNL", "supported", "cursor-next-line"),
            Entry("CSI:E", "CNL duplicate", "partial", null));

        VtCapabilityManifestException error = Assert.Throws<VtCapabilityManifestException>(
            () => VtCapabilityCatalog.Parse(json));

        Assert.Contains("CSI:E", error.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsDuplicateContractCases()
    {
        string json = Manifest(
            Entry("CSI:E", "CNL", "supported", "cursor-line"),
            Entry("CSI:F", "CPL", "supported", "cursor-line"));

        VtCapabilityManifestException error = Assert.Throws<VtCapabilityManifestException>(
            () => VtCapabilityCatalog.Parse(json));

        Assert.Contains("cursor-line", error.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsUnknownSupportValue()
    {
        string json = Manifest(Entry("CSI:E", "CNL", "complete", "cursor-next-line"));

        VtCapabilityManifestException error = Assert.Throws<VtCapabilityManifestException>(
            () => VtCapabilityCatalog.Parse(json));

        Assert.Contains("CSI:E", error.Message, StringComparison.Ordinal);
        Assert.Contains("complete", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMissingDescription()
    {
        string json = """
        {
          "schemaVersion": 1,
          "capabilities": [
            {
              "key": "CSI:E",
              "mnemonic": "CNL",
              "support": "supported",
              "description": "",
              "matrixFeature": "CNL (E)",
              "evidencePath": "tests/NovaTerminal.VT.Tests/CursorLinePositioningTests.cs",
              "contractCase": "cursor-next-line"
            }
          ]
        }
        """;

        VtCapabilityManifestException error = Assert.Throws<VtCapabilityManifestException>(
            () => VtCapabilityCatalog.Parse(json));

        Assert.Contains("CSI:E", error.Message, StringComparison.Ordinal);
        Assert.Contains("description", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("evidencePath")]
    [InlineData("contractCase")]
    public void Parse_RejectsSupportedEntryWithoutRequiredContractField(string field)
    {
        string entry = Entry("CSI:E", "CNL", "supported", "cursor-next-line")
            .Replace($"\"{field}\": \"{(field == "evidencePath" ? "tests/NovaTerminal.VT.Tests/CursorLinePositioningTests.cs" : "cursor-next-line")}\"", $"\"{field}\": null", StringComparison.Ordinal);

        VtCapabilityManifestException error = Assert.Throws<VtCapabilityManifestException>(
            () => VtCapabilityCatalog.Parse(Manifest(entry)));

        Assert.Contains("CSI:E", error.Message, StringComparison.Ordinal);
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    private static string Manifest(params string[] entries)
        => $$"""
        {
          "schemaVersion": 1,
          "capabilities": [
            {{string.Join(",\n", entries)}}
          ]
        }
        """;

    private static string Entry(string key, string mnemonic, string support, string? contractCase)
        => $$"""
            {
              "key": "{{key}}",
              "mnemonic": "{{mnemonic}}",
              "support": "{{support}}",
              "description": "{{mnemonic}} description",
              "matrixFeature": "{{mnemonic}} ({{key[^1]}})",
              "evidencePath": "tests/NovaTerminal.VT.Tests/CursorLinePositioningTests.cs",
              "contractCase": {{(contractCase is null ? "null" : $"\"{contractCase}\"")}}
            }
        """;
}
