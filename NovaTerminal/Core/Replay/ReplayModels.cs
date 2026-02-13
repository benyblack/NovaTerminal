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
    }

    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(ReplayHeader))]
    [JsonSerializable(typeof(ReplayEvent))]
    internal partial class ReplayJsonContext : JsonSerializerContext
    {
    }
}
