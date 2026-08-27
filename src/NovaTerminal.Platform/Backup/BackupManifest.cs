using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaTerminal.Platform.Backup;

/// <summary>
/// The <c>manifest.json</c> at the root of every bundle. camelCase on the wire — note this
/// differs from settings.json, which is PascalCase on disk and must stay that way.
/// </summary>
public sealed record BackupManifest
{
    /// <summary>Bundle format version. Bump when the on-disk layout changes incompatibly.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string AppVersion { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public string Machine { get; init; } = string.Empty;
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupManifest))]
internal partial class BackupJsonContext : JsonSerializerContext
{
}
