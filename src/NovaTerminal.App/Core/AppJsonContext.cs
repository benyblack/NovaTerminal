using System.Collections.Generic;
using System;
using System.Text.Json.Serialization;
using NovaTerminal.Core;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.Core
{
    [JsonSerializable(typeof(TerminalSettings))]
    [JsonSerializable(typeof(TerminalProfile))]
    [JsonSerializable(typeof(TerminalTheme))]
    [JsonSerializable(typeof(ForwardingRule))]
    [JsonSerializable(typeof(DateTimeOffset))]
    [JsonSerializable(typeof(List<TerminalProfile>))]
    [JsonSerializable(typeof(List<TabTemplateRule>))]
    [JsonSerializable(typeof(List<ForwardingRule>))]
    [JsonSerializable(typeof(List<CommandHistoryEntry>))]
    [JsonSerializable(typeof(List<CommandSnippet>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(WorkspacePolicyHooks))]
    [JsonSourceGenerationOptions(WriteIndented = true, Converters = new[] { typeof(JsonColorConverter), typeof(TermColorJsonConverter) })]
    public partial class AppJsonContext : JsonSerializerContext
    {
    }
}
