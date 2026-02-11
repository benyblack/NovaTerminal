using System.Collections.Generic;
using System.Text.Json.Serialization;
using NovaTerminal.Core;

namespace NovaTerminal.Core
{
    [JsonSerializable(typeof(TerminalSettings))]
    [JsonSerializable(typeof(TerminalProfile))]
    [JsonSerializable(typeof(TerminalTheme))]
    [JsonSerializable(typeof(ForwardingRule))]
    [JsonSerializable(typeof(List<TerminalProfile>))]
    [JsonSerializable(typeof(List<ForwardingRule>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSourceGenerationOptions(WriteIndented = true, Converters = new[] { typeof(JsonColorConverter) })]
    public partial class AppJsonContext : JsonSerializerContext
    {
    }
}
