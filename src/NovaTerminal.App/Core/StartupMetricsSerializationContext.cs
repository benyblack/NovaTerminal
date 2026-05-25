using System.Text.Json.Serialization;

namespace NovaTerminal.Core;

[JsonSerializable(typeof(StartupMetricsSnapshot))]
internal sealed partial class StartupMetricsSerializationContext : JsonSerializerContext
{
}
