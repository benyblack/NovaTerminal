using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace NovaTerminal.Core
{
    public sealed class TermColorJsonConverter : JsonConverter<TermColor>
    {
        public override TermColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string? s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return TermColor.Transparent;
                try
                {
                    var color = Color.Parse(s);
                    return TermColor.FromAvaloniaColor(color);
                }
                catch
                {
                    return TermColor.Transparent;
                }
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                // Backward-compatible object parsing for legacy/custom payloads.
                using var doc = JsonDocument.ParseValue(ref reader);
                byte r = 0, g = 0, b = 0, a = 255;
                bool hasAny = false;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Number) continue;
                    switch (prop.Name)
                    {
                        case "R":
                        case "r":
                            if (prop.Value.TryGetByte(out var rr)) { r = rr; hasAny = true; }
                            break;
                        case "G":
                        case "g":
                            if (prop.Value.TryGetByte(out var gg)) { g = gg; hasAny = true; }
                            break;
                        case "B":
                        case "b":
                            if (prop.Value.TryGetByte(out var bb)) { b = bb; hasAny = true; }
                            break;
                        case "A":
                        case "a":
                            if (prop.Value.TryGetByte(out var aa)) { a = aa; hasAny = true; }
                            break;
                    }
                }

                return hasAny ? new TermColor(r, g, b, a) : TermColor.Transparent;
            }

            if (reader.TokenType == JsonTokenType.Null)
            {
                return TermColor.Transparent;
            }

            throw new JsonException($"Unsupported token {reader.TokenType} for {nameof(TermColor)}.");
        }

        public override void Write(Utf8JsonWriter writer, TermColor value, JsonSerializerOptions options)
        {
            // Keep theme files human-editable and compatible with existing #RRGGBB format.
            if (value.A == 255)
            {
                writer.WriteStringValue($"#{value.R:X2}{value.G:X2}{value.B:X2}");
            }
            else
            {
                writer.WriteStringValue($"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");
            }
        }
    }
}
