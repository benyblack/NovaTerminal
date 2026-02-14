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
    [JsonSerializable(typeof(List<TabTemplateRule>))]
    [JsonSerializable(typeof(List<ForwardingRule>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(WorkspacePolicyHooks))]
    [JsonSourceGenerationOptions(WriteIndented = true, Converters = new[] { typeof(JsonColorConverter), typeof(TermColorJsonConverter) })]
    public partial class AppJsonContext : JsonSerializerContext
    {
    }
}
