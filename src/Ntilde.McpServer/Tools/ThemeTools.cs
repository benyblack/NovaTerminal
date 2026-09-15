using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace NovaTerminal.McpServer.Tools;

[McpServerToolType]
public static partial class ThemeTools
{
    // The 16 ANSI colors plus the 3 UI colors. All are required in a NovaTerminal theme JSON.
    private static readonly string[] ColorFields =
    {
        "Foreground", "Background", "CursorColor",
        "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White",
        "BrightBlack", "BrightRed", "BrightGreen", "BrightYellow",
        "BrightBlue", "BrightMagenta", "BrightCyan", "BrightWhite",
    };

    // Accept "#RGB", "#RRGGBB", "#RRGGBBAA" and the scRGB "sc#r,g,b[,a]" form used by JsonColorConverter.
    [GeneratedRegex(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColorRegex();

    [McpServerTool(Name = "novaterminal.get_theme_schema"),
     Description("Returns the schema for a NovaTerminal theme JSON file: required fields (Name + 16 ANSI colors + Foreground/Background/CursorColor), the accepted color string format, and an example. Use before authoring or editing a theme.")]
    public static string GetThemeSchema() =>
        """
        # NovaTerminal theme JSON schema

        A theme is a JSON object. All fields below are required.

        | Field | Type | Notes |
        |-------|------|-------|
        | `Name` | string | Display name, e.g. "Dracula". Non-empty. |
        | `Foreground` | color | Default text color. |
        | `Background` | color | Terminal background. |
        | `CursorColor` | color | Cursor color. |
        | `Black`,`Red`,`Green`,`Yellow`,`Blue`,`Magenta`,`Cyan`,`White` | color | Standard ANSI 0–7. |
        | `BrightBlack`…`BrightWhite` | color | Bright ANSI 8–15 (same 8 names, `Bright`-prefixed). |

        **Color format:** `#RGB`, `#RRGGBB`, or `#RRGGBBAA` (hex), or the scRGB `sc#r,g,b[,a]` form.

        ## Example
        ```json
        {
          "Name": "Dracula",
          "Foreground": "#F8F8F2", "Background": "#282A36", "CursorColor": "#F8F8F2",
          "Black": "#21222C", "Red": "#FF5555", "Green": "#50FA7B", "Yellow": "#F1FA8C",
          "Blue": "#BD93F9", "Magenta": "#FF79C6", "Cyan": "#8BE9FD", "White": "#F8F8F2",
          "BrightBlack": "#6272A4", "BrightRed": "#FF6E6E", "BrightGreen": "#69FF94",
          "BrightYellow": "#FFFFA5", "BrightBlue": "#D6ACFF", "BrightMagenta": "#FF92DF",
          "BrightCyan": "#A4FFFF", "BrightWhite": "#FFFFFF"
        }
        ```
        """;

    [McpServerTool(Name = "novaterminal.validate_theme_json"),
     Description("Validates a NovaTerminal theme JSON string against the theme schema. Reports missing required fields, invalid color values, and unknown fields. Returns a structured pass/fail report.")]
    public static string ValidateThemeJson(
        [Description("The full theme JSON document to validate.")] string themeJson)
    {
        if (string.IsNullOrWhiteSpace(themeJson))
        {
            return "INVALID: empty input — expected a theme JSON object.";
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(themeJson);
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

        // Name
        if (!root.TryGetProperty("Name", out var nameEl))
        {
            errors.Add("Missing required field 'Name'.");
        }
        else if (nameEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameEl.GetString()))
        {
            errors.Add("Field 'Name' must be a non-empty string.");
        }

        // Colors
        foreach (var field in ColorFields)
        {
            if (!root.TryGetProperty(field, out var el))
            {
                errors.Add($"Missing required color field '{field}'.");
                continue;
            }
            if (el.ValueKind != JsonValueKind.String)
            {
                errors.Add($"Field '{field}' must be a color string, but was {el.ValueKind}.");
                continue;
            }
            var value = el.GetString() ?? string.Empty;
            if (!IsValidColor(value))
            {
                errors.Add($"Field '{field}' has an invalid color value '{value}' (expected #RGB/#RRGGBB/#RRGGBBAA or sc#r,g,b).");
            }
        }

        // Unknown fields → warnings (forward-compatible, not fatal)
        var known = new HashSet<string>(ColorFields) { "Name" };
        foreach (var prop in root.EnumerateObject())
        {
            if (!known.Contains(prop.Name))
            {
                warnings.Add($"Unknown field '{prop.Name}' (ignored).");
            }
        }

        var sb = new StringBuilder();
        sb.Append(errors.Count == 0 ? "VALID" : "INVALID").Append('\n');
        if (errors.Count > 0)
        {
            sb.Append("\nErrors:\n");
            foreach (var e in errors) sb.Append("  - ").Append(e).Append('\n');
        }
        if (warnings.Count > 0)
        {
            sb.Append("\nWarnings:\n");
            foreach (var w in warnings) sb.Append("  - ").Append(w).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static bool IsValidColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (HexColorRegex().IsMatch(value)) return true;
        // scRGB form: sc#r,g,b or sc#r,g,b,a with float components.
        if (value.StartsWith("sc#", System.StringComparison.OrdinalIgnoreCase))
        {
            var parts = value[3..].Split(',');
            return parts.Length is 3 or 4 &&
                   parts.All(p => float.TryParse(p.Trim(), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out _));
        }
        return false;
    }
}
