using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace NovaTerminal.Core
{
    [JsonSerializable(typeof(NovaSession))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class SessionSerializationContext : JsonSerializerContext
    {
    }
}
