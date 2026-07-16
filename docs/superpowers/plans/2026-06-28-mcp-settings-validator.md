# MCP Settings Schema & Validation Tools — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `novaterminal.get_settings_schema` + `novaterminal.validate_settings_json` to the MCP Dev Companion, validating the top-level `settings.json` shape (depth A).

**Architecture:** A new static tool class `SettingsTools` in `src/NovaTerminal.McpServer`, mirroring `ConnectionProfileTools` (`[McpServerToolType]` / `[McpServerTool]`, `System.Text.Json` `JsonDocument` parsing, `VALID`/`INVALID` report). Validates top-level settings fields + structural shape of collections only — no deep per-profile validation. No server `ProjectReference`, no drift-guard test.

**Tech Stack:** C# / .NET 10, `ModelContextProtocol` SDK, `System.Text.Json`, xUnit v3.

## Global Constraints

- Build/test ONLY via wrappers: `scripts/build.ps1 <args>` / `scripts/build.sh <args>`. Never raw `dotnet` (hangs when stdout is captured).
- No `ProjectReference` added to `src/NovaTerminal.McpServer` (security isolation). Field knowledge is hand-mirrored.
- Wire format = **PascalCase keys + integer enums** (the embedded profile enums; settings scalars are bool/double/int/string). Not camelCase, not string enums.
- Report format mirrors `ThemeTools`/`ConnectionProfileTools`: output starts with `VALID` or `INVALID`; optional `Errors:` and/or `Warnings:` blocks, lines `  - <msg>`; trailing whitespace trimmed. Validity = zero errors (warnings never fail). `"INVALID".StartsWith("VALID")` is false → keep `Assert.StartsWith` in tests.
- **No required fields:** `{}` is VALID.
- Tool names exactly `novaterminal.get_settings_schema`, `novaterminal.validate_settings_json`.
- **No reflection drift-guard** for settings (would need a heavy `NovaTerminal.App` reference). The known-field list is hand-maintained with a comment pointing to `src/NovaTerminal.App/Shell/TerminalSettings.cs`.

**Reference — the 32 serialized top-level fields** (source of truth: `src/NovaTerminal.App/Shell/TerminalSettings.cs`; `ThemeManager` and `ActiveTheme` are `[JsonIgnore]` and excluded):
- **bool (14):** `EnableLigatures`, `EnableComplexShaping`, `CursorBlink`, `BellAudioEnabled`, `BellVisualEnabled`, `SmoothScrolling`, `EnableLinkDetection`, `QuakeModeEnabled`, `CommandAssistEnabled`, `CommandAssistHistoryEnabled`, `CommandAssistAutoHideInAltScreen`, `CommandAssistShellIntegrationEnabled`, `CommandAssistPowerShellIntegrationEnabled`, `ExperimentalNativeSshEnabled`
- **number/double (4):** `FontSize` (>0), `WindowOpacity` (0–1), `BackgroundImageOpacity` (0–1), `WheelLinesPerNotch` (warn ≤0)
- **integer (2):** `MaxHistory` (≥0), `CommandAssistMaxHistoryEntries` (≥0)
- **string (8):** `FontFamily`, `ThemeName`, `BackgroundImagePath`, `GlobalHotkey`, and enum-like (type-check only) `BlurEffect`, `CursorStyle`, `PaneClosePolicy`, `BackgroundImageStretch`
- **GUID string (1):** `DefaultProfileId`
- **object string→string (1):** `Keybindings`
- **array (2):** `Profiles`, `TabTemplateRules`

---

### Task 1: Schema tool (`get_settings_schema`)

**Files:**
- Create: `src/NovaTerminal.McpServer/Tools/SettingsTools.cs`
- Test: `tests/NovaTerminal.McpServer.Tests/SettingsToolsTests.cs`

**Interfaces:**
- Produces: `public static string SettingsTools.GetSettingsSchema()` on a `[McpServerToolType] public static class SettingsTools`. (Task 2 adds `ValidateSettingsJson` + helpers to the same class.)

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.McpServer.Tests/SettingsToolsTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~SettingsToolsTests"`
Expected: FAIL — build error, `SettingsTools` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/NovaTerminal.McpServer/Tools/SettingsTools.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace NovaTerminal.McpServer.Tools;

// Source of truth for the field list: src/NovaTerminal.App/Shell/TerminalSettings.cs
// (PascalCase keys, integer enums; ThemeManager and ActiveTheme are [JsonIgnore]).
// This server has NO ProjectReference to NovaTerminal.App by design, so the field knowledge
// is hand-mirrored. There is intentionally no reflection drift-guard (an App reference would
// pull in Avalonia); keep this list in sync with TerminalSettings by hand.
[McpServerToolType]
public static class SettingsTools
{
    [McpServerTool(Name = "novaterminal.get_settings_schema"),
     Description("Returns the schema for NovaTerminal's settings.json: top-level fields grouped by area (PascalCase keys, integer enums for embedded profiles), types, defaults, and an annotated example. Validates the top-level shape only (embedded profiles are not deep-validated). Use before authoring or editing settings.json.")]
    public static string GetSettingsSchema() =>
        """
        # NovaTerminal settings.json schema

        Settings are stored at `%LOCALAPPDATA%\NovaTerminal\settings.json` (override dir via
        `NOVATERM_APPDATA_ROOT`). The on-disk format uses **PascalCase** field names and
        **integer-valued enums** (for embedded profiles). Every field has a default, so an empty
        object `{}` is valid. This tool/validator covers the top-level fields and the structural
        shape of collections; it does not deep-validate embedded profile entries.

        ## Appearance
        | Field | Type | Notes |
        |-------|------|-------|
        | `FontSize` | number | > 0. Default 14. |
        | `FontFamily` | string | Default "Cascadia Mono PL". |
        | `ThemeName` | string | Default "Default". |
        | `WindowOpacity` | number | 0–1. Default 1.0. |
        | `BlurEffect` | string (enum-like) | e.g. "Acrylic", "Mica", "None". Type-checked only. |
        | `EnableLigatures` | bool | Default false. |
        | `EnableComplexShaping` | bool | Default true. |
        | `CursorStyle` | string (enum-like) | e.g. "Underline", "Block", "Bar"/"Beam". Type-checked only. |
        | `CursorBlink` | bool | Default true. |

        ## Behavior
        | Field | Type | Notes |
        |-------|------|-------|
        | `MaxHistory` | integer | >= 0. Default 10000. |
        | `BellAudioEnabled` | bool | Default true. |
        | `BellVisualEnabled` | bool | Default true. |
        | `SmoothScrolling` | bool | Default true. |
        | `EnableLinkDetection` | bool | Default true. |
        | `WheelLinesPerNotch` | number | > 0 (≤ 0 falls back to 3.0). Default 3.0. |
        | `PaneClosePolicy` | string (enum-like) | e.g. "Confirm", "Force". Type-checked only. |
        | `QuakeModeEnabled` | bool | Default true. |
        | `GlobalHotkey` | string | Default "Alt+OemTilde". |
        | `ExperimentalNativeSshEnabled` | bool | Default false. |

        ## Background
        | Field | Type | Notes |
        |-------|------|-------|
        | `BackgroundImagePath` | string | Default "". |
        | `BackgroundImageOpacity` | number | 0–1. Default 0.5. |
        | `BackgroundImageStretch` | string (enum-like) | "None"/"Fill"/"Uniform"/"UniformToFill". Type-checked only. |

        ## Command Assist
        | Field | Type | Notes |
        |-------|------|-------|
        | `CommandAssistEnabled` | bool | Default false. |
        | `CommandAssistHistoryEnabled` | bool | Default true. |
        | `CommandAssistMaxHistoryEntries` | integer | >= 0. Default 5000. |
        | `CommandAssistAutoHideInAltScreen` | bool | Default true. |
        | `CommandAssistShellIntegrationEnabled` | bool | Default true. |
        | `CommandAssistPowerShellIntegrationEnabled` | bool | Default true. |

        ## Collections
        | Field | Type | Notes |
        |-------|------|-------|
        | `Keybindings` | object | Map of string → string. |
        | `TabTemplateRules` | array | Tab template rule objects (not deep-validated here). |
        | `Profiles` | array | Terminal profile objects (not deep-validated here). |
        | `DefaultProfileId` | string (GUID) | Must be a valid GUID. |

        Enum-like string values above are non-authoritative guidance — the runtime parses them
        case-insensitively with fallbacks, so the validator only checks they are strings.

        ## Example
        ```json
        {
          "FontSize": 14,
          "MaxHistory": 10000,
          "FontFamily": "Cascadia Mono PL",
          "ThemeName": "Default",
          "WindowOpacity": 1.0,
          "BlurEffect": "Acrylic",
          "EnableLigatures": false,
          "EnableComplexShaping": true,
          "CursorStyle": "Underline",
          "CursorBlink": true,
          "BellAudioEnabled": true,
          "BellVisualEnabled": true,
          "SmoothScrolling": true,
          "EnableLinkDetection": true,
          "WheelLinesPerNotch": 3.0,
          "PaneClosePolicy": "Confirm",
          "Keybindings": { "Ctrl+Shift+C": "copy" },
          "TabTemplateRules": [],
          "BackgroundImagePath": "",
          "BackgroundImageOpacity": 0.5,
          "BackgroundImageStretch": "UniformToFill",
          "QuakeModeEnabled": true,
          "GlobalHotkey": "Alt+OemTilde",
          "CommandAssistEnabled": false,
          "CommandAssistHistoryEnabled": true,
          "CommandAssistMaxHistoryEntries": 5000,
          "CommandAssistAutoHideInAltScreen": true,
          "CommandAssistShellIntegrationEnabled": true,
          "CommandAssistPowerShellIntegrationEnabled": true,
          "ExperimentalNativeSshEnabled": false,
          "Profiles": [
            { "Id": "00000000-0000-0000-0000-000000000001", "Name": "Command Prompt", "Command": "cmd.exe", "Type": 0 }
          ],
          "DefaultProfileId": "00000000-0000-0000-0000-000000000001"
        }
        ```
        """;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~SettingsToolsTests"`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.McpServer/Tools/SettingsTools.cs tests/NovaTerminal.McpServer.Tests/SettingsToolsTests.cs
git commit -m "feat(mcp): add get_settings_schema tool"
```

---

### Task 2: Validator tool (`validate_settings_json`)

**Files:**
- Modify: `src/NovaTerminal.McpServer/Tools/SettingsTools.cs`
- Test: `tests/NovaTerminal.McpServer.Tests/SettingsToolsTests.cs`

**Interfaces:**
- Consumes: `GetSettingsSchema()` (self-consistency test).
- Produces: `public static string SettingsTools.ValidateSettingsJson(string settingsJson)`.

- [ ] **Step 1: Write the failing tests**

Append inside the `SettingsToolsTests` class:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~SettingsToolsTests"`
Expected: FAIL — build error, `ValidateSettingsJson` does not exist.

- [ ] **Step 3: Write the implementation**

Add these `using` directives at the top of `SettingsTools.cs` (alongside the existing two):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
```

Add the following members inside the `SettingsTools` class (after `GetSettingsSchema`):

```csharp
    private static readonly string[] BoolFields =
    {
        "EnableLigatures", "EnableComplexShaping", "CursorBlink", "BellAudioEnabled",
        "BellVisualEnabled", "SmoothScrolling", "EnableLinkDetection", "QuakeModeEnabled",
        "CommandAssistEnabled", "CommandAssistHistoryEnabled", "CommandAssistAutoHideInAltScreen",
        "CommandAssistShellIntegrationEnabled", "CommandAssistPowerShellIntegrationEnabled",
        "ExperimentalNativeSshEnabled",
    };

    // Plain + enum-like strings; enum-like values are NOT value-validated (type-check only).
    private static readonly string[] StringFields =
    {
        "FontFamily", "ThemeName", "BackgroundImagePath", "GlobalHotkey",
        "BlurEffect", "CursorStyle", "PaneClosePolicy", "BackgroundImageStretch",
    };

    private static readonly string[] ArrayFields = { "Profiles", "TabTemplateRules" };

    // Every recognized top-level field (union of all groups). Source of truth: TerminalSettings.cs.
    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "FontSize", "MaxHistory", "FontFamily", "ThemeName", "WindowOpacity", "BlurEffect",
        "EnableLigatures", "EnableComplexShaping", "CursorStyle", "CursorBlink",
        "BellAudioEnabled", "BellVisualEnabled", "SmoothScrolling", "EnableLinkDetection",
        "WheelLinesPerNotch", "PaneClosePolicy", "Keybindings", "TabTemplateRules",
        "BackgroundImagePath", "BackgroundImageOpacity", "BackgroundImageStretch",
        "QuakeModeEnabled", "GlobalHotkey", "CommandAssistEnabled", "CommandAssistHistoryEnabled",
        "CommandAssistMaxHistoryEntries", "CommandAssistAutoHideInAltScreen",
        "CommandAssistShellIntegrationEnabled", "CommandAssistPowerShellIntegrationEnabled",
        "ExperimentalNativeSshEnabled", "Profiles", "DefaultProfileId",
    };

    [McpServerTool(Name = "novaterminal.validate_settings_json"),
     Description("Validates a NovaTerminal settings.json string (the top-level shape). Reports wrong field types, out-of-range numerics, a malformed DefaultProfileId GUID, malformed collection shapes, and warns on unknown fields and any stray Password. Embedded profiles are not deep-validated. An empty object {} is valid (every setting has a default).")]
    public static string ValidateSettingsJson(
        [Description("The settings.json document to validate.")] string settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return "INVALID: empty input — expected a settings JSON object.";
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
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

        foreach (var field in BoolFields)
        {
            RequireBool(root, field, errors);
        }
        foreach (var field in StringFields)
        {
            RequireString(root, field, errors);
        }
        foreach (var field in ArrayFields)
        {
            RequireArray(root, field, errors);
        }

        // Numbers with ranges.
        CheckNumber(root, "FontSize", v => v > 0, "must be > 0", errors);
        CheckNumber(root, "WindowOpacity", v => v >= 0 && v <= 1, "must be between 0 and 1", errors);
        CheckNumber(root, "BackgroundImageOpacity", v => v >= 0 && v <= 1, "must be between 0 and 1", errors);

        // WheelLinesPerNotch: a non-number is an error; <= 0 is only a warning (runtime falls back).
        if (root.TryGetProperty("WheelLinesPerNotch", out var wheelEl))
        {
            if (wheelEl.ValueKind != JsonValueKind.Number || !wheelEl.TryGetDouble(out var wheel))
            {
                errors.Add($"Field 'WheelLinesPerNotch' must be a number, but was {wheelEl.ValueKind}.");
            }
            else if (wheel <= 0)
            {
                warnings.Add("Field 'WheelLinesPerNotch' is <= 0; the runtime falls back to 3.0.");
            }
        }

        // Integers with min.
        RequireIntMin(root, "MaxHistory", 0, errors);
        RequireIntMin(root, "CommandAssistMaxHistoryEntries", 0, errors);

        // DefaultProfileId: GUID string. A non-GUID breaks deserialization of the whole file.
        if (root.TryGetProperty("DefaultProfileId", out var idEl))
        {
            if (idEl.ValueKind != JsonValueKind.String)
            {
                errors.Add($"Field 'DefaultProfileId' must be a string GUID, but was {idEl.ValueKind}.");
            }
            else if (!Guid.TryParse(idEl.GetString(), out _))
            {
                errors.Add("Field 'DefaultProfileId' must be a valid GUID; an unparseable value makes the store fail to load settings.json.");
            }
        }

        // Keybindings: object of string -> string.
        if (root.TryGetProperty("Keybindings", out var kbEl))
        {
            if (kbEl.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"Field 'Keybindings' must be a JSON object (string -> string), but was {kbEl.ValueKind}.");
            }
            else
            {
                foreach (var entry in kbEl.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.String)
                    {
                        errors.Add($"Keybindings['{entry.Name}'] must be a string, but was {entry.Value.ValueKind}.");
                    }
                }
            }
        }

        // Unknown fields + stray Password (top-level only; profiles are not deep-scanned).
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, "Password", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Field '{prop.Name}' must not appear in settings JSON — passwords are stored in the credential vault, not in settings.");
                continue;
            }

            if (!KnownFields.Contains(prop.Name))
            {
                warnings.Add($"Unknown field '{prop.Name}' (ignored).");
            }
        }

        return Report(errors, warnings);
    }

    private static void RequireBool(JsonElement root, string field, List<string> errors)
    {
        if (root.TryGetProperty(field, out var el)
            && el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add($"Field '{field}' must be a boolean, but was {el.ValueKind}.");
        }
    }

    private static void RequireString(JsonElement root, string field, List<string> errors)
    {
        if (root.TryGetProperty(field, out var el) && el.ValueKind != JsonValueKind.String)
        {
            errors.Add($"Field '{field}' must be a string, but was {el.ValueKind}.");
        }
    }

    private static void RequireArray(JsonElement root, string field, List<string> errors)
    {
        if (root.TryGetProperty(field, out var el) && el.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"Field '{field}' must be a JSON array, but was {el.ValueKind}.");
        }
    }

    private static void CheckNumber(
        JsonElement root, string field, Func<double, bool> inRange, string rangeMsg, List<string> errors)
    {
        if (!root.TryGetProperty(field, out var el))
        {
            return;
        }

        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var v))
        {
            errors.Add($"Field '{field}' must be a number, but was {el.ValueKind}.");
            return;
        }

        if (!inRange(v))
        {
            errors.Add($"Field '{field}' value {v} is out of range ({rangeMsg}).");
        }
    }

    private static void RequireIntMin(JsonElement root, string field, int min, List<string> errors)
    {
        if (!root.TryGetProperty(field, out var el))
        {
            return;
        }

        if (el.ValueKind != JsonValueKind.Number)
        {
            errors.Add($"Field '{field}' must be an integer, but was {el.ValueKind}.");
            return;
        }

        if (!el.TryGetInt32(out var v))
        {
            errors.Add($"Field '{field}' must be an integer (no decimals).");
            return;
        }

        if (v < min)
        {
            errors.Add($"Field '{field}' value {v} is out of range (>= {min}).");
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

Note on `MaxHistory: 1.5` → `TryGetInt32` returns false for a non-integral JSON number, so it is reported as "must be an integer" (the `FractionalIntField_IsError` test).

- [ ] **Step 4: Run tests to verify they pass**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests --filter "FullyQualifiedName~SettingsToolsTests"`
Expected: PASS (all `SettingsToolsTests`).

- [ ] **Step 5: Run the whole test project**

Run: `scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests`
Expected: PASS — existing tests plus the new ones.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.McpServer/Tools/SettingsTools.cs tests/NovaTerminal.McpServer.Tests/SettingsToolsTests.cs
git commit -m "feat(mcp): add validate_settings_json tool"
```

---

### Task 3: Documentation updates

**Files:**
- Modify: `docs/mcp/tools.md`
- Modify: `docs/mcp-dev-companion.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Add the two tools to the tools table and bump the version**

In `docs/mcp/tools.md`, change the title line `# NovaTerminal MCP Dev Companion — tools (v0.3)` to `(v0.4)`.

Then add these two rows to the tools table, immediately after the `novaterminal.validate_connection_profile_json` row:

```markdown
| `novaterminal.get_settings_schema` | — | The settings.json schema: top-level fields by area (PascalCase, integer enums for embedded profiles), types, defaults, and an example. Top-level shape only. |
| `novaterminal.validate_settings_json` | `settingsJson` | Validates a settings.json string (top-level shape); reports wrong types, out-of-range numerics, malformed `DefaultProfileId`, bad collection shapes, and warns on unknown fields and any stray `Password`. Embedded profiles are not deep-validated. |
```

- [ ] **Step 2: Note the depth in the Notes section**

In `docs/mcp/tools.md`, append this bullet to the `## Notes` section:

```markdown
- `validate_settings_json` validates the top-level settings shape and the structure of its
  collections; it does not deep-validate embedded `Profiles`/`TabTemplateRules` entries.
```

- [ ] **Step 3: List the new tools in the companion overview**

In `docs/mcp-dev-companion.md`, in the tools paragraph that points at `mcp/tools.md`, add the two new tool names alongside the connection-profile ones:

```markdown
`novaterminal.get_connection_profile_schema`, `novaterminal.validate_connection_profile_json`,
`novaterminal.get_settings_schema`, `novaterminal.validate_settings_json`.
```

(If the existing text lists only the connection-profile tools, append the two settings tools to that same sentence.)

- [ ] **Step 4: Commit**

```bash
git add docs/mcp/tools.md docs/mcp-dev-companion.md
git commit -m "docs(mcp): document settings schema & validation tools"
```

---

## Notes for the implementer

- **Raw string literal:** `GetSettingsSchema` uses a `"""` raw string; preserve the closing `"""` indentation exactly, matching `ThemeTools.GetThemeSchema` / `ConnectionProfileTools.GetConnectionProfileSchema`.
- **`VALID` vs `INVALID`:** `"INVALID".StartsWith("VALID")` is false, so `Assert.StartsWith("VALID", ...)` distinguishes them — do not change to `Contains`.
- **Implicit usings:** the McpServer project has `ImplicitUsings` enabled, so some `using`s may be redundant; redundant file-level usings that duplicate a global using are harmless. If the build reports an *unnecessary using* error (warnings-as-errors), drop the redundant ones to keep output pristine.
- **No CI change:** `NovaTerminal.McpServer.Tests` is already in CI's unit-test loop; no new project, no `ci.yml` change.
- **Reviewer awareness:** Greptile auto-reviews on PR open and every push; Gemini auto-reviews on open; Codex reviews on `@codex review`.
