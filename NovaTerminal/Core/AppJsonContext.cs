using System.Collections.Generic;
using System.Text.Json.Serialization;
using NovaTerminal.Core;

namespace NovaTerminal.Core
{
    [JsonSerializable(typeof(TerminalSettings))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    public partial class AppJsonContext : JsonSerializerContext
    {
    }
}
