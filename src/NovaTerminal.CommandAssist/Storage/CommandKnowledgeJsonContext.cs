using System.Text.Json.Serialization;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Storage;

/// <summary>
/// Source-generated deserialization context for the bundled command-knowledge catalogue (V2 Phase 4b).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="CommandAssistJsonContext"/> because the two have nothing in common but
/// the assembly they live in: that one round-trips the user's mutable stores with
/// <c>WriteIndented = true</c>, this one only ever reads one immutable embedded asset and never
/// writes. Options are baked into a generated context, so "read-only, no indentation opinion" is a
/// second context rather than a second call.
/// </para>
/// <para>
/// Source-generated rather than reflection-based for the reason the storage contexts are: the app
/// publishes with <c>PublishAot</c>, where reflection-based deserialization of these record types
/// would be trimmed away and fail at runtime rather than at build time.
/// </para>
/// </remarks>
[JsonSerializable(typeof(CommandKnowledgeCatalogue))]
[JsonSourceGenerationOptions(WriteIndented = false)]
public partial class CommandKnowledgeJsonContext : JsonSerializerContext
{
}
