using System;
using System.Text.Json.Serialization;

namespace NovaTerminal.Core.Replay
{
    public class ReplayHeader
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("v")]
        public int Version { get; set; } = 2;

        [JsonPropertyName("cols")]
        public int Cols { get; set; }

        [JsonPropertyName("rows")]
        public int Rows { get; set; }

        [JsonPropertyName("date")]
        public string Date { get; set; } = DateTime.UtcNow.ToString("O");

        [JsonPropertyName("shell")]
        public string Shell { get; set; } = "";
    }

    public class ReplayEvent
    {
        [JsonPropertyName("t")]
        public long TimeOffsetMs { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "data";

        [JsonPropertyName("d")]
        public string? Data { get; set; }

        [JsonPropertyName("cols")]
        public int? Cols { get; set; }

        [JsonPropertyName("rows")]
        public int? Rows { get; set; }

        [JsonPropertyName("n")]
        public string? MarkerName { get; set; }

        [JsonPropertyName("i")]
        public string? Input { get; set; }

        [JsonPropertyName("s")]
        public ReplaySnapshot? Snapshot { get; set; }
    }

    public class ReplaySnapshot
    {
        [JsonPropertyName("cols")] public int Cols { get; set; }
        [JsonPropertyName("rows")] public int Rows { get; set; }
        [JsonPropertyName("cx")] public int CursorCol { get; set; }
        [JsonPropertyName("cy")] public int CursorRow { get; set; }
        [JsonPropertyName("alt")] public bool IsAltScreen { get; set; }
        [JsonPropertyName("st")] public int ScrollTop { get; set; }
        [JsonPropertyName("sb")] public int ScrollBottom { get; set; }

        [JsonPropertyName("awm")] public bool IsAutoWrapMode { get; set; }
        [JsonPropertyName("ckm")] public bool IsApplicationCursorKeys { get; set; }
        [JsonPropertyName("decom")] public bool IsOriginMode { get; set; }
        [JsonPropertyName("bp")] public bool IsBracketedPasteMode { get; set; }
        [JsonPropertyName("cv")] public bool IsCursorVisible { get; set; }

        [JsonPropertyName("fg")] public uint CurrentForeground { get; set; }
        [JsonPropertyName("bg")] public uint CurrentBackground { get; set; }
        [JsonPropertyName("fgi")] public short CurrentFgIndex { get; set; }
        [JsonPropertyName("bgi")] public short CurrentBgIndex { get; set; }
        [JsonPropertyName("dfg")] public bool IsDefaultForeground { get; set; }
        [JsonPropertyName("dbg")] public bool IsDefaultBackground { get; set; }
        [JsonPropertyName("inv")] public bool IsInverse { get; set; }
        [JsonPropertyName("bold")] public bool IsBold { get; set; }
        [JsonPropertyName("faint")] public bool IsFaint { get; set; }
        [JsonPropertyName("italic")] public bool IsItalic { get; set; }
        [JsonPropertyName("ul")] public bool IsUnderline { get; set; }
        [JsonPropertyName("blink")] public bool IsBlink { get; set; }
        [JsonPropertyName("strike")] public bool IsStrikethrough { get; set; }
        [JsonPropertyName("hidden")] public bool IsHidden { get; set; }

        [JsonPropertyName("cells")]
        public string? CellsBase64 { get; set; }

        [JsonPropertyName("ext")]
        public System.Collections.Generic.Dictionary<int, string>? ExtendedText { get; set; }

        [JsonPropertyName("wrap")]
        public bool[]? RowWraps { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(ReplayHeader))]
    [JsonSerializable(typeof(ReplayEvent))]
    [JsonSerializable(typeof(ReplaySnapshot))]
    public partial class ReplayJsonContext : JsonSerializerContext
    {
    }
}
