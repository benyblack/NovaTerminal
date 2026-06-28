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
