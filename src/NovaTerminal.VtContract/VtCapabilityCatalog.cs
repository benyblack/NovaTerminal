using System.Reflection;
using System.Text.Json;

namespace NovaTerminal.VtContract;

public enum VtSupport
{
    Supported,
    Partial,
    Unsupported,
}

public sealed record VtCapability(
    string Key,
    string Mnemonic,
    VtSupport Support,
    string Description,
    string MatrixFeature,
    string? EvidencePath,
    string? ContractCase);

public sealed class VtCapabilityManifestException : Exception
{
    public VtCapabilityManifestException(string message)
        : base(message)
    {
    }

    public VtCapabilityManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class VtCapabilityCatalog
{
    private const string ResourceName = "NovaTerminal.VtContract.vt-capabilities.json";

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<VtCapability> All { get; } = LoadEmbeddedManifest();

    public static IReadOnlyList<VtCapability> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ManifestDocument>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new VtCapabilityManifestException("The VT capability manifest is not valid JSON.", exception);
        }

        if (document is null)
        {
            throw new VtCapabilityManifestException("The VT capability manifest is empty.");
        }

        if (document.SchemaVersion != 1)
        {
            throw new VtCapabilityManifestException($"Unsupported VT capability manifest schemaVersion '{document.SchemaVersion}'.");
        }

        if (document.Capabilities is null)
        {
            throw new VtCapabilityManifestException("The VT capability manifest must contain a capabilities array.");
        }

        var capabilities = new List<VtCapability>(document.Capabilities.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var contractCases = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManifestEntry entry in document.Capabilities)
        {
            string key = Require(entry.Key, "key", "<unknown>");
            if (!keys.Add(key))
            {
                throw new VtCapabilityManifestException($"Capability '{key}' has a duplicate key.");
            }

            string mnemonic = Require(entry.Mnemonic, "mnemonic", key);
            string description = Require(entry.Description, "description", key);
            string matrixFeature = Require(entry.MatrixFeature, "matrixFeature", key);
            if (!Enum.TryParse(entry.Support, ignoreCase: true, out VtSupport support))
            {
                throw new VtCapabilityManifestException($"Capability '{key}' has unknown support value '{entry.Support}'.");
            }

            string? evidencePath = NullIfWhiteSpace(entry.EvidencePath);
            string? contractCase = NullIfWhiteSpace(entry.ContractCase);
            if (contractCase is not null && !contractCases.Add(contractCase))
            {
                throw new VtCapabilityManifestException($"Capability '{key}' has duplicate contractCase '{contractCase}'.");
            }

            if (support == VtSupport.Supported && evidencePath is null)
            {
                throw new VtCapabilityManifestException($"Capability '{key}' is supported but has no evidencePath.");
            }

            if (support == VtSupport.Supported && contractCase is null)
            {
                throw new VtCapabilityManifestException($"Capability '{key}' is supported but has no contractCase.");
            }

            capabilities.Add(new VtCapability(
                key,
                mnemonic,
                support,
                description,
                matrixFeature,
                evidencePath,
                contractCase));
        }

        return capabilities.AsReadOnly();
    }

    private static IReadOnlyList<VtCapability> LoadEmbeddedManifest()
    {
        Assembly assembly = typeof(VtCapabilityCatalog).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new VtCapabilityManifestException($"Embedded VT capability manifest '{ResourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static string Require(string? value, string fieldName, string key)
    {
        string? normalized = NullIfWhiteSpace(value);
        return normalized
            ?? throw new VtCapabilityManifestException($"Capability '{key}' must provide {fieldName}.");
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ManifestDocument(int SchemaVersion, IReadOnlyList<ManifestEntry>? Capabilities);

    private sealed record ManifestEntry(
        string? Key,
        string? Mnemonic,
        string? Support,
        string? Description,
        string? MatrixFeature,
        string? EvidencePath,
        string? ContractCase);
}
