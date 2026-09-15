using System.Text.Json.Serialization;

namespace Ntilde.Shell;

[JsonSerializable(typeof(StartupMetricsSnapshot))]
internal sealed partial class StartupMetricsSerializationContext : JsonSerializerContext
{
}
