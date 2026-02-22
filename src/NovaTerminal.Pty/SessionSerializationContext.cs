using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace NovaTerminal.Core
{
    [JsonSerializable(typeof(NovaSession))]
    [JsonSerializable(typeof(WorkspaceBundlePackage))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    public partial class SessionSerializationContext : JsonSerializerContext
    {
    }
}
