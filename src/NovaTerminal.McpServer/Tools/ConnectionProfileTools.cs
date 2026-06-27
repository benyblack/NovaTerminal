using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace NovaTerminal.McpServer.Tools;

// Source of truth for field names / enum values:
//   src/NovaTerminal.Platform/Ssh/Models/SshProfile.cs (+ SshJumpHop, PortForward,
//   SshMuxOptions) and SshProfileStoreSnapshot in Ssh/Storage/ISshProfileStore.cs.
// This server has NO ProjectReference to NovaTerminal.Platform by design, so that
// knowledge is hand-mirrored below. A reflection drift-guard test in
// NovaTerminal.McpServer.Tests fails if those types change. Keep both in sync.
[McpServerToolType]
public static class ConnectionProfileTools
{
    [McpServerTool(Name = "novaterminal.get_connection_profile_schema"),
     Description("Returns the schema for a NovaTerminal SSH connection-profile JSON: PascalCase fields grouped by area, integer enum mappings, defaults, and a complete example. The on-disk format uses integer-valued enums; passwords are vault-managed and never stored in profile JSON. Use before authoring or editing a profile.")]
    public static string GetConnectionProfileSchema() =>
        """
        # NovaTerminal connection-profile (SSH) JSON schema

        Profiles are stored at `%LOCALAPPDATA%\NovaTerminal\ssh\profiles.json`. The on-disk
        format uses **PascalCase** field names and **integer-valued enums**. Passwords are
        never stored here — they live in the OS credential vault.

        A single profile is a JSON object. The full file wraps profiles in a document:
        `{ "SchemaVersion": 1, "Profiles": [ <profile>, ... ] }`. The validator accepts
        either shape.

        ## Identity / organization
        | Field | Type | Notes |
        |-------|------|-------|
        | `Id` | string (GUID) | Optional; the store assigns one if absent/invalid. |
        | `Name` | string | **Required**, non-empty. |
        | `GroupPath` | string | Tree path, e.g. "Prod/DB". Default "General". |
        | `Notes` | string | Free text. |
        | `AccentColor` | string | e.g. "#3399EE". |
        | `Tags` | string[] | Labels. |

        ## Connection
        | Field | Type | Notes |
        |-------|------|-------|
        | `Host` | string | **Required**, non-empty. |
        | `User` | string | SSH user. |
        | `Port` | int | 1–65535. Default 22. |
        | `BackendKind` | int enum | 0=OpenSsh, 1=Native. |

        ## Auth
        | Field | Type | Notes |
        |-------|------|-------|
        | `AuthMode` | int enum | 0=Default, 1=Agent, 2=IdentityFile. |
        | `IdentityFilePath` | string | Key path; expected when AuthMode is 2 (IdentityFile). |
        | `RememberPasswordInVault` | bool | Whether to keep the password in the vault. |

        ## Jump hosts
        `JumpHops` is an array of `{ "Host": string, "User": string, "Port": int (1–65535) }`.

        ## Port forwarding
        `Forwards` is an array of objects:
        | Field | Type | Notes |
        |-------|------|-------|
        | `Kind` | int enum | 0=Local, 1=Remote, 2=Dynamic. |
        | `BindAddress` | string | e.g. "127.0.0.1". |
        | `SourcePort` | int | 0–65535. |
        | `DestinationHost` | string | Forward target host. |
        | `DestinationPort` | int | 0–65535. |

        ## Multiplexing
        `MuxOptions` is `{ "Enabled": bool, "ControlMasterAuto": bool, "ControlPath": string, "ControlPersistSeconds": int (>= 0) }`.

        ## Session / shell
        | Field | Type | Notes |
        |-------|------|-------|
        | `ServerAliveIntervalSeconds` | int | >= 1. Default 30. |
        | `ServerAliveCountMax` | int | >= 1. Default 3. |
        | `ExtraSshArgs` | string | Extra ssh CLI args. |
        | `WorkingDirectory` | string | Remote working directory. |
        | `RemoteShellKind` | int enum | 0=Auto, 1=Bash, 2=Zsh, 3=Fish, 4=Pwsh. |

        ## Document wrapper
        | Field | Type | Notes |
        |-------|------|-------|
        | `SchemaVersion` | int | Current = 1. |
        | `Profiles` | object[] | Array of profile objects. |

        ## Example (full document)
        ```json
        {
          "SchemaVersion": 1,
          "Profiles": [
            {
              "Id": "aeb456e4-8dd2-4b33-a74d-cb473de0323b",
              "BackendKind": 0,
              "Name": "prod-db",
              "GroupPath": "Prod/DB",
              "Notes": "primary database jump",
              "AccentColor": "#3399EE",
              "Tags": ["prod", "db"],
              "Host": "prod.internal",
              "User": "svc",
              "Port": 2222,
              "AuthMode": 2,
              "IdentityFilePath": "/home/svc/.ssh/prod_ed25519",
              "RememberPasswordInVault": false,
              "JumpHops": [
                { "Host": "jump-1.internal", "User": "ops", "Port": 22 }
              ],
              "Forwards": [
                { "Kind": 0, "BindAddress": "127.0.0.1", "SourcePort": 5432, "DestinationHost": "db.internal", "DestinationPort": 5432 }
              ],
              "MuxOptions": { "Enabled": false, "ControlMasterAuto": true, "ControlPath": "", "ControlPersistSeconds": 0 },
              "ServerAliveIntervalSeconds": 30,
              "ServerAliveCountMax": 3,
              "ExtraSshArgs": "",
              "WorkingDirectory": "",
              "RemoteShellKind": 0
            }
          ]
        }
        ```
        """;

    private const int CurrentSchemaVersion = 1;

    // Known PascalCase field names (mirror SshProfile etc.; guarded by drift-guard test).
    internal static readonly string[] ProfileFields =
    {
        "Id", "BackendKind", "Name", "GroupPath", "Notes", "AccentColor", "Tags", "Host",
        "User", "Port", "AuthMode", "IdentityFilePath", "RememberPasswordInVault",
        "JumpHops", "Forwards", "MuxOptions", "ServerAliveIntervalSeconds",
        "ServerAliveCountMax", "ExtraSshArgs", "WorkingDirectory", "RemoteShellKind",
    };
    internal static readonly string[] JumpHopFields = { "Host", "User", "Port" };
    internal static readonly string[] PortForwardFields =
        { "Kind", "BindAddress", "SourcePort", "DestinationHost", "DestinationPort" };
    internal static readonly string[] MuxFields =
        { "Enabled", "ControlMasterAuto", "ControlPath", "ControlPersistSeconds" };
    internal static readonly string[] DocumentFields = { "SchemaVersion", "Profiles" };

    // Enum names indexed by integer value (mirror the enums; guarded by drift-guard test).
    internal static readonly string[] BackendKindNames = { "OpenSsh", "Native" };
    internal static readonly string[] AuthModeNames = { "Default", "Agent", "IdentityFile" };
    internal static readonly string[] PortForwardKindNames = { "Local", "Remote", "Dynamic" };
    internal static readonly string[] RemoteShellKindNames = { "Auto", "Bash", "Zsh", "Fish", "Pwsh" };

    [McpServerTool(Name = "novaterminal.validate_connection_profile_json"),
     Description("Validates a NovaTerminal SSH connection-profile JSON string. Accepts either a single profile object or a full profiles.json document ({ \"SchemaVersion\", \"Profiles\": [...] }); auto-detects which. Reports errors (wrong types, out-of-range integer enums/ports, missing required Name/Host) and warnings (unknown fields, a stray Password field, malformed Id, etc.). Passwords are vault-managed and must never appear in profile JSON.")]
    public static string ValidateConnectionProfileJson(
        [Description("The connection-profile JSON to validate: a single SshProfile object or a full profiles.json document.")] string profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return "INVALID: empty input — expected a connection-profile JSON object.";
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(profileJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return $"INVALID: not valid JSON — {ex.Message}";
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return $"INVALID: top-level value must be a JSON object, but was {root.ValueKind}.";
        }

        var errors = new List<string>();
        var warnings = new List<string>();

        if (root.TryGetProperty("Profiles", out var profilesEl))
        {
            ValidateDocument(root, profilesEl, errors, warnings);
        }
        else
        {
            ValidateProfile(root, string.Empty, errors, warnings);
        }

        return Report(errors, warnings);
    }

    private static void ValidateDocument(
        JsonElement root, JsonElement profilesEl, List<string> errors, List<string> warnings)
    {
        if (!root.TryGetProperty("SchemaVersion", out var verEl))
        {
            warnings.Add("'SchemaVersion' is missing; the store defaults it to 1.");
        }
        else if (verEl.ValueKind != JsonValueKind.Number || !verEl.TryGetInt32(out var ver))
        {
            errors.Add($"Field 'SchemaVersion' must be an integer, but was {verEl.ValueKind}.");
        }
        else if (ver <= 0)
        {
            warnings.Add($"'SchemaVersion' is {ver}; the store defaults non-positive versions to 1.");
        }
        else if (ver > CurrentSchemaVersion)
        {
            warnings.Add($"'SchemaVersion' is {ver}, newer than the current version {CurrentSchemaVersion}; some fields may be unrecognized.");
        }

        if (profilesEl.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"Field 'Profiles' must be a JSON array, but was {profilesEl.ValueKind}.");
        }
        else
        {
            int i = 0;
            foreach (var profile in profilesEl.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"'Profiles[{i}]' must be a JSON object, but was {profile.ValueKind}.");
                }
                else
                {
                    ValidateProfile(profile, $"Profiles[{i}].", errors, warnings);
                }
                i++;
            }
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name is not ("SchemaVersion" or "Profiles"))
            {
                warnings.Add($"Unknown field '{prop.Name}' (ignored).");
            }
        }
    }

    private static void ValidateProfile(
        JsonElement p, string path, List<string> errors, List<string> warnings)
    {
        RequireNonEmptyString(p, path, "Name", errors);
        RequireNonEmptyString(p, path, "Host", errors);

        foreach (var f in new[]
                 {
                     "GroupPath", "Notes", "AccentColor", "User",
                     "IdentityFilePath", "ExtraSshArgs", "WorkingDirectory",
                 })
        {
            RequireStringType(p, path, f, errors);
        }

        if (p.TryGetProperty("Id", out var idEl))
        {
            if (idEl.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{path}Field 'Id' must be a string GUID, but was {idEl.ValueKind}.");
            }
            else if (!Guid.TryParse(idEl.GetString(), out _))
            {
                warnings.Add($"{path}Field 'Id' is not a valid GUID; the store will assign one.");
            }
        }

        if (p.TryGetProperty("Tags", out var tagsEl))
        {
            if (tagsEl.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{path}Field 'Tags' must be an array of strings, but was {tagsEl.ValueKind}.");
            }
            else
            {
                int ti = 0;
                foreach (var tag in tagsEl.EnumerateArray())
                {
                    if (tag.ValueKind != JsonValueKind.String)
                    {
                        errors.Add($"{path}Field 'Tags[{ti}]' must be a string, but was {tag.ValueKind}.");
                    }
                    ti++;
                }
            }
        }

        RequireBoolType(p, path, "RememberPasswordInVault", errors);

        CheckEnum(p, path, "BackendKind", BackendKindNames, errors);
        CheckEnum(p, path, "AuthMode", AuthModeNames, errors);
        CheckEnum(p, path, "RemoteShellKind", RemoteShellKindNames, errors);

        CheckIntRange(p, path, "Port", 1, 65535, errors);
        CheckIntRange(p, path, "ServerAliveIntervalSeconds", 1, int.MaxValue, errors);
        CheckIntRange(p, path, "ServerAliveCountMax", 1, int.MaxValue, errors);

        if (p.TryGetProperty("AuthMode", out var amEl)
            && amEl.ValueKind == JsonValueKind.Number
            && amEl.TryGetInt32(out var am) && am == 2)
        {
            bool blank = !p.TryGetProperty("IdentityFilePath", out var ipEl)
                         || ipEl.ValueKind != JsonValueKind.String
                         || string.IsNullOrWhiteSpace(ipEl.GetString());
            if (blank)
            {
                warnings.Add($"{path}AuthMode is IdentityFile (2) but 'IdentityFilePath' is blank.");
            }
        }

        if (p.TryGetProperty("JumpHops", out var hopsEl))
        {
            ValidateArrayOfObjects(hopsEl, path, "JumpHops", errors, warnings, ValidateJumpHop);
        }

        if (p.TryGetProperty("Forwards", out var fwdEl))
        {
            ValidateArrayOfObjects(fwdEl, path, "Forwards", errors, warnings, ValidateForward);
        }

        if (p.TryGetProperty("MuxOptions", out var muxEl))
        {
            if (muxEl.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}Field 'MuxOptions' must be a JSON object, but was {muxEl.ValueKind}.");
            }
            else
            {
                ValidateMux(muxEl, $"{path}MuxOptions.", errors, warnings);
            }
        }

        CheckUnknownAndPassword(p, path, ProfileFields, errors, warnings);
    }

    private static void ValidateJumpHop(
        JsonElement h, string path, List<string> errors, List<string> warnings)
    {
        RequireStringType(h, path, "Host", errors);
        RequireStringType(h, path, "User", errors);
        CheckIntRange(h, path, "Port", 1, 65535, errors);
        CheckUnknownAndPassword(h, path, JumpHopFields, errors, warnings);
    }

    private static void ValidateForward(
        JsonElement f, string path, List<string> errors, List<string> warnings)
    {
        CheckEnum(f, path, "Kind", PortForwardKindNames, errors);
        RequireStringType(f, path, "BindAddress", errors);
        RequireStringType(f, path, "DestinationHost", errors);
        CheckIntRange(f, path, "SourcePort", 0, 65535, errors);
        CheckIntRange(f, path, "DestinationPort", 0, 65535, errors);

        if (f.TryGetProperty("Kind", out var kEl)
            && kEl.ValueKind == JsonValueKind.Number
            && kEl.TryGetInt32(out var kind) && (kind == 0 || kind == 1))
        {
            WarnZeroPort(f, path, "SourcePort", warnings);
            WarnZeroPort(f, path, "DestinationPort", warnings);
        }

        CheckUnknownAndPassword(f, path, PortForwardFields, errors, warnings);
    }

    private static void ValidateMux(
        JsonElement m, string path, List<string> errors, List<string> warnings)
    {
        RequireBoolType(m, path, "Enabled", errors);
        RequireBoolType(m, path, "ControlMasterAuto", errors);
        RequireStringType(m, path, "ControlPath", errors);
        CheckIntRange(m, path, "ControlPersistSeconds", 0, int.MaxValue, errors);
        CheckUnknownAndPassword(m, path, MuxFields, errors, warnings);
    }

    private static void RequireNonEmptyString(
        JsonElement obj, string path, string field, List<string> errors)
    {
        if (!obj.TryGetProperty(field, out var el))
        {
            errors.Add($"{path}Missing required field '{field}'.");
        }
        else if (el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
        {
            errors.Add($"{path}Field '{field}' must be a non-empty string.");
        }
    }

    private static void RequireStringType(
        JsonElement obj, string path, string field, List<string> errors)
    {
        if (obj.TryGetProperty(field, out var el) && el.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}Field '{field}' must be a string, but was {el.ValueKind}.");
        }
    }

    private static void RequireBoolType(
        JsonElement obj, string path, string field, List<string> errors)
    {
        if (obj.TryGetProperty(field, out var el)
            && el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add($"{path}Field '{field}' must be a boolean, but was {el.ValueKind}.");
        }
    }

    private static void CheckEnum(
        JsonElement obj, string path, string field, string[] names, List<string> errors)
    {
        if (!obj.TryGetProperty(field, out var el))
        {
            return;
        }

        string mapping = string.Join(", ", names.Select((n, i) => $"{i}={n}"));
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
        {
            errors.Add($"{path}Field '{field}' must be an integer enum value ({mapping}), but was {el.ValueKind}.");
            return;
        }

        if (v < 0 || v >= names.Length)
        {
            errors.Add($"{path}Field '{field}' value {v} is out of range ({mapping}).");
        }
    }

    private static void CheckIntRange(
        JsonElement obj, string path, string field, int min, int max, List<string> errors)
    {
        if (!obj.TryGetProperty(field, out var el))
        {
            return;
        }

        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
        {
            errors.Add($"{path}Field '{field}' must be an integer, but was {el.ValueKind}.");
            return;
        }

        if (v < min || v > max)
        {
            string range = max == int.MaxValue ? $">= {min}" : $"[{min}..{max}]";
            errors.Add($"{path}Field '{field}' value {v} is out of range ({range}).");
        }
    }

    private static void WarnZeroPort(
        JsonElement obj, string path, string field, List<string> warnings)
    {
        if (obj.TryGetProperty(field, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out var v) && v == 0)
        {
            warnings.Add($"{path}Field '{field}' is 0 for a Local/Remote forward; expected a real port.");
        }
    }

    private static void CheckUnknownAndPassword(
        JsonElement obj, string path, string[] known, List<string> errors, List<string> warnings)
    {
        var set = new HashSet<string>(known, StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, "Password", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{path}Field '{prop.Name}' must not appear in profile JSON — passwords are stored in the credential vault, not in profiles.");
                continue;
            }

            if (!set.Contains(prop.Name))
            {
                warnings.Add($"{path}Unknown field '{prop.Name}' (ignored).");
            }
        }
    }

    private static void ValidateArrayOfObjects(
        JsonElement arr, string parentPath, string field,
        List<string> errors, List<string> warnings,
        Action<JsonElement, string, List<string>, List<string>> validateItem)
    {
        if (arr.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{parentPath}Field '{field}' must be a JSON array, but was {arr.ValueKind}.");
            return;
        }

        int i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{parentPath}{field}[{i}] must be a JSON object, but was {item.ValueKind}.");
            }
            else
            {
                validateItem(item, $"{parentPath}{field}[{i}].", errors, warnings);
            }
            i++;
        }
    }

    private static string Report(List<string> errors, List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.Append(errors.Count == 0 ? "VALID" : "INVALID").Append('\n');
        if (errors.Count > 0)
        {
            sb.Append("\nErrors:\n");
            foreach (var e in errors)
            {
                sb.Append("  - ").Append(e).Append('\n');
            }
        }
        if (warnings.Count > 0)
        {
            sb.Append("\nWarnings:\n");
            foreach (var w in warnings)
            {
                sb.Append("  - ").Append(w).Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }
}
