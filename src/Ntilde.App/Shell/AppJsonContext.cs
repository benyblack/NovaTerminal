using System.Collections.Generic;
using System;
using System.Text.Json.Serialization;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Shell.Shortcuts;

namespace Ntilde.Shell
{
    [JsonSerializable(typeof(TerminalSettings))]
    [JsonSerializable(typeof(TerminalProfile))]
    [JsonSerializable(typeof(TerminalTheme))]
    [JsonSerializable(typeof(ForwardingRule))]
    [JsonSerializable(typeof(DateTimeOffset))]
    [JsonSerializable(typeof(List<TerminalProfile>))]
    [JsonSerializable(typeof(List<TabTemplateRule>))]
    [JsonSerializable(typeof(List<ForwardingRule>))]
    // Command Assist storage types moved to Ntilde.CommandAssist's own
    // CommandAssistJsonContext when that assembly was extracted; nothing in App serializes them.
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, CommandPaletteUsageEntry>))]
    [JsonSerializable(typeof(WorkspacePolicyHooks))]
    [JsonSourceGenerationOptions(WriteIndented = true, Converters = new[] { typeof(JsonColorConverter), typeof(TermColorJsonConverter) })]
    public partial class AppJsonContext : JsonSerializerContext
    {
    }
}
