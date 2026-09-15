using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Storage;

/// <summary>
/// Source-generated serialization context for Command Assist's on-disk stores.
/// </summary>
/// <remarks>
/// These types were previously registered on the App's <c>AppJsonContext</c>. Keeping them there
/// would force this assembly to reference the UI project; a local context keeps the stores
/// AOT-safe (the app publishes with <c>PublishAot</c>) without that dependency. Options mirror
/// <c>AppJsonContext</c>'s <c>WriteIndented = true</c> so the existing <c>history.json</c> and
/// <c>snippets.json</c> files round-trip byte-identically. The color converters registered on
/// <c>AppJsonContext</c> are irrelevant here: no Command Assist storage type carries a color.
/// </remarks>
[JsonSerializable(typeof(CommandHistoryEntry))]
[JsonSerializable(typeof(CommandSnippet))]
[JsonSerializable(typeof(List<CommandHistoryEntry>))]
[JsonSerializable(typeof(List<CommandSnippet>))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class CommandAssistJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Source-generated context for the JSON-Lines history file.
/// </summary>
/// <remarks>
/// Identical to <see cref="CommandAssistJsonContext"/> except for <c>WriteIndented = false</c>: a
/// JSONL record has to be exactly one line, and options are baked into a source-generated context,
/// so the two formats need two contexts. Reflection-based options are not an option here - the app
/// publishes with <c>PublishAot</c>.
/// </remarks>
[JsonSerializable(typeof(CommandHistoryEntry))]
[JsonSourceGenerationOptions(WriteIndented = false)]
public partial class CommandAssistJsonLinesContext : JsonSerializerContext
{
}
