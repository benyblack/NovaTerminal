using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace Ntilde.Pty
{
    [JsonSerializable(typeof(NtildeSession))]
    [JsonSerializable(typeof(WorkspaceBundlePackage))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    public partial class SessionSerializationContext : JsonSerializerContext
    {
    }
}
