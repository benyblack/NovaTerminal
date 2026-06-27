# MCP Connection-Profile Schema & Validation Tools — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two read-only MCP tools — `novaterminal.get_connection_profile_schema` and `novaterminal.validate_connection_profile_json` — to the NovaTerminal MCP Dev Companion, targeting the real on-disk SSH connection-profile format.

**Architecture:** A new static tool class `ConnectionProfileTools` in `src/NovaTerminal.McpServer`, mirroring the existing `ThemeTools` pattern (`[McpServerToolType]` / `[McpServerTool]`, `string` return, `System.Text.Json` parsing). The server keeps its no-`ProjectReference` isolation, so field/enum knowledge is hand-mirrored from `SshProfile`; a reflection-based drift-guard test in the *test* project (which gains a test-only `ProjectReference` to `NovaTerminal.Platform`) fails if `SshProfile` changes.

**Tech Stack:** C# / .NET 10, `ModelContextProtocol` SDK, `System.Text.Json`, xUnit v3.

## Global Constraints

- **Build/test only via wrappers:** `scripts/build.ps1 <args>` (PowerShell) or `scripts/build.sh <args>` (bash). Never raw `dotnet build`/`dotnet test` (it hangs when stdout is captured).
- **Server isolation:** `src/NovaTerminal.McpServer` must NOT gain any `ProjectReference`. Field/enum knowledge is hand-written. (The *test* project may reference `NovaTerminal.Platform` — that is the only new cross-project edge allowed.)
- **Wire format is verified fact:** on-disk profile JSON uses **PascalCase keys** and **integer-valued enums** (`SshJsonContext` sets only `WriteIndented`; no naming policy, no string-enum converter). Do not use camelCase or string enums anywhere.
- **Report format matches `ThemeTools`:** output starts with `VALID` or `INVALID`; an `Errors:` block and/or `Warnings:` block follow, each line `  - <message>`; trailing whitespace trimmed. Validity = zero errors (warnings never fail).
- **Tool names exactly:** `novaterminal.get_connection_profile_schema`, `novaterminal.validate_connection_profile_json`.
- **TargetFramework** is centralized in `Directory.Build.props` (`net10.0`); do not add it to csproj files.
- Commit after each task.

**Reference — exact source-of-truth types** (`src/NovaTerminal.Platform/Ssh/Models/`):
- `SshProfile` props (21): `Id, BackendKind, Name, GroupPath, Notes, AccentColor, Tags, Host, User, Port, AuthMode, IdentityFilePath, RememberPasswordInVault, JumpHops, Forwards, MuxOptions, ServerAliveIntervalSeconds, ServerAliveCountMax, ExtraSshArgs, WorkingDirectory, RemoteShellKind`
- `SshJumpHop` props (3): `Host, User, Port`
- `PortForward` props (5): `Kind, BindAddress, SourcePort, DestinationHost, DestinationPort`
- `SshMuxOptions` props (4): `Enabled, ControlMasterAuto, ControlPath, ControlPersistSeconds`
- `SshProfileStoreSnapshot` props (2, public, namespace `NovaTerminal.Platform.Ssh.Storage`): `SchemaVersion, Profiles`
- Enums: `SshBackendKind {OpenSsh=0, Native=1}` (ns `NovaTerminal.Platform.Ssh.Models`); `SshAuthMode {Default=0, Agent=1, IdentityFile=2}` (ns `…Ssh.Models`); `PortForwardKind {Local=0, Remote=1, Dynamic=2}` (ns `…Ssh.Models`); `RemoteShellKind {Auto=0, Bash=1, Zsh=2, Fish=3, Pwsh=4}` (ns `NovaTerminal.Platform`)

---

### Task 1: Schema tool (`get_connection_profile_schema`)

**Files:**
- Create: `src/NovaTerminal.McpServer/Tools/ConnectionProfileTools.cs`
- Test: `tests/NovaTerminal.McpServer.Tests/ConnectionProfileToolsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static string ConnectionProfileTools.GetConnectionProfileSchema()` — returns the descriptive schema text including a fenced ` ```json ` example. The class is `public static` and annotated `[McpServerToolType]`. (Task 2 adds `ValidateConnectionProfileJson` to the same class; Task 3's drift guard reads `internal` arrays added in Task 2.)

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.McpServer.Tests/ConnectionProfileToolsTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~ConnectionProfileToolsTests"`
Expected: FAIL — build error, `ConnectionProfileTools` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/NovaTerminal.McpServer/Tools/ConnectionProfileTools.cs`:

```csharp
using System.ComponentModel;
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
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~ConnectionProfileToolsTests"`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.McpServer/Tools/ConnectionProfileTools.cs tests/NovaTerminal.McpServer.Tests/ConnectionProfileToolsTests.cs
git commit -m "feat(mcp): add get_connection_profile_schema tool"
```

---

### Task 2: Validator tool (`validate_connection_profile_json`)

**Files:**
- Modify: `src/NovaTerminal.McpServer/Tools/ConnectionProfileTools.cs`
- Test: `tests/NovaTerminal.McpServer.Tests/ConnectionProfileToolsTests.cs`

**Interfaces:**
- Consumes: `GetConnectionProfileSchema()` (for the self-consistency test).
- Produces:
  - `public static string ConnectionProfileTools.ValidateConnectionProfileJson(string profileJson)` — `VALID`/`INVALID` report.
  - `internal static readonly string[] ProfileFields, JumpHopFields, PortForwardFields, MuxFields, DocumentFields` — known PascalCase field-name sets (read by Task 3's drift guard).
  - `internal static readonly string[] BackendKindNames, AuthModeNames, PortForwardKindNames, RemoteShellKindNames` — enum names indexed by integer value (read by Task 3's drift guard).

- [ ] **Step 1: Write the failing tests**

Append to `tests/NovaTerminal.McpServer.Tests/ConnectionProfileToolsTests.cs` (inside the class):

```csharp
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
    public void MalformedId_IsWarning()
    {
        var result = ConnectionProfileTools.ValidateConnectionProfileJson("""{ "Name": "n", "Host": "h", "Id": "not-a-guid" }""");
        Assert.StartsWith("VALID", result);
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~ConnectionProfileToolsTests"`
Expected: FAIL — build error, `ValidateConnectionProfileJson` does not exist.

- [ ] **Step 3: Write the implementation**

Add `using` directives at the top of `ConnectionProfileTools.cs` (alongside the existing two):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
```

Then add the following members inside the `ConnectionProfileTools` class (after `GetConnectionProfileSchema`):

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~ConnectionProfileToolsTests"`
Expected: PASS (all `ConnectionProfileToolsTests`, ~24 tests).

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.McpServer/Tools/ConnectionProfileTools.cs tests/NovaTerminal.McpServer.Tests/ConnectionProfileToolsTests.cs
git commit -m "feat(mcp): add validate_connection_profile_json tool"
```

---

### Task 3: Drift-guard test (reflection vs `NovaTerminal.Platform`)

**Files:**
- Modify: `tests/NovaTerminal.McpServer.Tests/NovaTerminal.McpServer.Tests.csproj`
- Create: `tests/NovaTerminal.McpServer.Tests/ConnectionProfileDriftGuardTests.cs`

**Interfaces:**
- Consumes: `ConnectionProfileTools.ProfileFields/JumpHopFields/PortForwardFields/MuxFields/DocumentFields` and `…BackendKindNames/AuthModeNames/PortForwardKindNames/RemoteShellKindNames` (internal, visible via the existing `InternalsVisibleTo`); the real types from `NovaTerminal.Platform`.
- Produces: nothing (test-only).

- [ ] **Step 1: Add the test-only ProjectReference**

Edit `tests/NovaTerminal.McpServer.Tests/NovaTerminal.McpServer.Tests.csproj` — add `NovaTerminal.Platform` to the existing `ProjectReference` ItemGroup so it reads:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\NovaTerminal.McpServer\NovaTerminal.McpServer.csproj" />
    <!-- Test-only: the drift guard reflects over the real profile types. The MCP server
         itself must never reference NovaTerminal.Platform. -->
    <ProjectReference Include="..\..\src\NovaTerminal.Platform\NovaTerminal.Platform.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing drift-guard tests**

Create `tests/NovaTerminal.McpServer.Tests/ConnectionProfileDriftGuardTests.cs`:

```csharp
using System;
using System.Linq;
using NovaTerminal.McpServer.Tools;
using NovaTerminal.Platform;                 // RemoteShellKind
using NovaTerminal.Platform.Ssh.Models;      // SshProfile, SshJumpHop, PortForward, SshMuxOptions, enums
using NovaTerminal.Platform.Ssh.Storage;     // SshProfileStoreSnapshot

namespace NovaTerminal.McpServer.Tests;

// Guards against drift between the hand-mirrored field/enum knowledge in
// ConnectionProfileTools and the real types in NovaTerminal.Platform. If these fail,
// a profile type changed — update ConnectionProfileTools (fields, enums, schema, rules).
public class ConnectionProfileDriftGuardTests
{
    private static string[] PropNames(Type t) =>
        t.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string[] Sorted(string[] values) =>
        values.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void ProfileFields_MatchSshProfile() =>
        Assert.Equal(PropNames(typeof(SshProfile)), Sorted(ConnectionProfileTools.ProfileFields));

    [Fact]
    public void JumpHopFields_MatchSshJumpHop() =>
        Assert.Equal(PropNames(typeof(SshJumpHop)), Sorted(ConnectionProfileTools.JumpHopFields));

    [Fact]
    public void PortForwardFields_MatchPortForward() =>
        Assert.Equal(PropNames(typeof(PortForward)), Sorted(ConnectionProfileTools.PortForwardFields));

    [Fact]
    public void MuxFields_MatchSshMuxOptions() =>
        Assert.Equal(PropNames(typeof(SshMuxOptions)), Sorted(ConnectionProfileTools.MuxFields));

    [Fact]
    public void DocumentFields_MatchSnapshot() =>
        Assert.Equal(PropNames(typeof(SshProfileStoreSnapshot)), Sorted(ConnectionProfileTools.DocumentFields));

    // Enum name arrays are indexed by integer value; compare to Enum.GetNames (value order).
    [Fact]
    public void BackendKindNames_MatchEnum() =>
        Assert.Equal(Enum.GetNames<SshBackendKind>(), ConnectionProfileTools.BackendKindNames);

    [Fact]
    public void AuthModeNames_MatchEnum() =>
        Assert.Equal(Enum.GetNames<SshAuthMode>(), ConnectionProfileTools.AuthModeNames);

    [Fact]
    public void PortForwardKindNames_MatchEnum() =>
        Assert.Equal(Enum.GetNames<PortForwardKind>(), ConnectionProfileTools.PortForwardKindNames);

    [Fact]
    public void RemoteShellKindNames_MatchEnum() =>
        Assert.Equal(Enum.GetNames<RemoteShellKind>(), ConnectionProfileTools.RemoteShellKindNames);
}
```

- [ ] **Step 3: Run drift-guard tests to verify they pass**

(The arrays and types already exist from Task 2, so these should pass once the reference is added.)

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~ConnectionProfileDriftGuardTests"`
Expected: PASS (9 tests). If any fail, the hand-mirrored array in `ConnectionProfileTools` disagrees with the real type — fix the array (and the schema text / validation rules) to match.

- [ ] **Step 4: Run the whole MCP test project to confirm nothing regressed**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests`
Expected: PASS (all existing tests plus the new ones).

- [ ] **Step 5: Commit**

```bash
git add tests/NovaTerminal.McpServer.Tests/NovaTerminal.McpServer.Tests.csproj tests/NovaTerminal.McpServer.Tests/ConnectionProfileDriftGuardTests.cs
git commit -m "test(mcp): add SshProfile drift-guard for connection-profile tools"
```

---

### Task 4: Documentation updates

**Files:**
- Modify: `docs/mcp/tools.md`
- Modify: `docs/mcp-dev-companion.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Add the two tools to the tools table and bump the version title**

In `docs/mcp/tools.md`, change the title line:

```markdown
# NovaTerminal MCP Dev Companion — tools (v0.1)
```

to:

```markdown
# NovaTerminal MCP Dev Companion — tools (v0.3)
```

Then add these two rows to the tools table, immediately after the `novaterminal.validate_theme_json` row:

```markdown
| `novaterminal.get_connection_profile_schema` | — | The SSH connection-profile JSON schema: PascalCase fields by area, integer enum mappings, defaults, and an example. Accepts a single profile or a full `profiles.json` document. |
| `novaterminal.validate_connection_profile_json` | `profileJson` | Validates a connection-profile JSON (single profile or full document; auto-detected); reports wrong types, out-of-range integer enums/ports, missing `Name`/`Host`, and warns on unknown fields and any stray `Password`. |
```

- [ ] **Step 2: Remove the now-shipped deferred entry**

In `docs/mcp/tools.md`, delete this bullet from the `## Still deferred` section (leave the other two bullets intact):

```markdown
- **Connection-profile schema/validation** (`get_connection_profile_schema`,
  `validate_connection_profile_json`): unlike themes, the profile format has no stable documented
  schema, is large (~37 fields incl. enums/nested rules), and contains a sensitive `Password`
  field — deferred until a documented profile schema exists, to avoid drift and over-coupling.
```

- [ ] **Step 3: List the new tools in the companion overview**

In `docs/mcp-dev-companion.md`, replace the tools paragraph:

```markdown
See [mcp/tools.md](mcp/tools.md). v0.1 exposes:

`novaterminal.get_project_summary`, `novaterminal.get_architecture_map`,
`novaterminal.list_docs`, `novaterminal.read_doc`,
`novaterminal.get_vt_conformance_summary`,
`novaterminal.get_theme_schema`, `novaterminal.validate_theme_json`,
`novaterminal.generate_codex_prompt_for_issue`.
```

with (adds the connection-profile tools and points at the authoritative list):

```markdown
See [mcp/tools.md](mcp/tools.md) for the authoritative list. As of v0.3 this includes the
connection-profile tools:

`novaterminal.get_connection_profile_schema`, `novaterminal.validate_connection_profile_json`.
```

- [ ] **Step 4: Commit**

```bash
git add docs/mcp/tools.md docs/mcp-dev-companion.md
git commit -m "docs(mcp): document connection-profile schema & validation tools"
```

---

## Notes for the implementer

- **Raw string literals:** `GetConnectionProfileSchema` uses a `"""` raw string. Preserve the closing `"""` indentation exactly as shown (it determines leading-whitespace stripping), matching the existing `ThemeTools.GetThemeSchema`.
- **`VALID` vs `INVALID` assertions:** `"INVALID".StartsWith("VALID")` is `false`, so `Assert.StartsWith("VALID", result)` correctly distinguishes them — do not change to `Contains`.
- **Pre-existing doc drift (out of scope):** `docs/mcp-dev-companion.md`'s old list omitted the v0.2 tools (`explain_escape_sequence`, `generate_vt_test_plan`, `suggest_relevant_files`). Task 4 redirects that section to `tools.md` as the source of truth rather than re-listing everything, so this plan does not separately backfill those names.
- **CI:** `NovaTerminal.McpServer.Tests` is already in the unit-test loop; no `ci.yml` change is needed for the new test files. The new test-only `ProjectReference` to `NovaTerminal.Platform` builds in the test job (other test projects already depend on it).
```
