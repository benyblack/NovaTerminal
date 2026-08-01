using System.Collections.Generic;
using System;
using System.Text.Json.Serialization;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Shell.Shortcuts;

namespace NovaTerminal.Shell
{
    [JsonSerializable(typeof(TerminalSettings))]
    [JsonSerializable(typeof(TerminalProfile))]
    [JsonSerializable(typeof(TerminalTheme))]
    [JsonSerializable(typeof(ForwardingRule))]
    [JsonSerializable(typeof(DateTimeOffset))]
    [JsonSerializable(typeof(List<TerminalProfile>))]
    [JsonSerializable(typeof(List<TabTemplateRule>))]
    [JsonSerializable(typeof(List<ForwardingRule>))]
    // Command Assist storage types moved to NovaTerminal.CommandAssist's own
    // CommandAssistJsonContext when that assembly was extracted; nothing in App serializes them.
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, CommandPaletteUsageEntry>))]
    [JsonSerializable(typeof(WorkspacePolicyHooks))]
    [JsonSourceGenerationOptions(WriteIndented = true, Converters = new[] { typeof(JsonColorConverter), typeof(TermColorJsonConverter) })]
    public partial class AppJsonContext : JsonSerializerContext
    {
    }
}
