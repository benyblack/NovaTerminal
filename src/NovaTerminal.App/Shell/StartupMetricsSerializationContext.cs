using System.Text.Json.Serialization;

namespace NovaTerminal.Shell;

[JsonSerializable(typeof(StartupMetricsSnapshot))]
internal sealed partial class StartupMetricsSerializationContext : JsonSerializerContext
{
}
