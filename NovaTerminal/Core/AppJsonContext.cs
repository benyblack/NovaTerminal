using System.Collections.Generic;
using System.Text.Json.Serialization;
using NovaTerminal.Core;

namespace NovaTerminal.Core
{
    [JsonSerializable(typeof(TerminalSettings))]
    [JsonSerializable(typeof(TerminalProfile))]
    [JsonSerializable(typeof(ForwardingRule))]
    [JsonSerializable(typeof(List<TerminalProfile>))]
    [JsonSerializable(typeof(List<ForwardingRule>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    public partial class AppJsonContext : JsonSerializerContext
    {
    }
}
