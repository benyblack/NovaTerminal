using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
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
        | `AgentAccessObserveEnabled` | bool | Default false. Enables the local agent-host observe endpoint (read-only session access for AI agents). |
        | `AgentReplayExportEnabled` | bool | Default false. Sub-gate on top of the observe toggle: allows agents to export a session's recent output as a replay file (output + resizes only, never input). |
        | `LongCommandNotificationsEnabled` | bool | Default false. In-app toast when a command that ran ≥30s finishes in an unfocused pane. |

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
          "AgentAccessObserveEnabled": false,
          "AgentReplayExportEnabled": false,
          "LongCommandNotificationsEnabled": false,
          "Profiles": [
            { "Id": "00000000-0000-0000-0000-000000000001", "Name": "Command Prompt", "Command": "cmd.exe", "Type": 0 }
          ],
          "DefaultProfileId": "00000000-0000-0000-0000-000000000001"
        }
        ```
        """;

    private static readonly string[] BoolFields =
    {
        "EnableLigatures", "EnableComplexShaping", "CursorBlink", "BellAudioEnabled",
        "BellVisualEnabled", "SmoothScrolling", "EnableLinkDetection", "QuakeModeEnabled",
        "CommandAssistEnabled", "CommandAssistHistoryEnabled", "CommandAssistAutoHideInAltScreen",
        "CommandAssistShellIntegrationEnabled", "CommandAssistPowerShellIntegrationEnabled",
        "ExperimentalNativeSshEnabled", "AgentAccessObserveEnabled", "AgentReplayExportEnabled",
        "LongCommandNotificationsEnabled",
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
        "ExperimentalNativeSshEnabled", "AgentAccessObserveEnabled", "AgentReplayExportEnabled",
        "LongCommandNotificationsEnabled", "Profiles", "DefaultProfileId",
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
            // TryGetInt32 fails both for fractional values and for whole numbers that overflow
            // a 32-bit int; distinguish so the message isn't misleading for large values.
            errors.Add(el.TryGetDouble(out var d) && d % 1 != 0
                ? $"Field '{field}' must be an integer (no decimals)."
                : $"Field '{field}' must be a whole number within the 32-bit integer range.");
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
}
