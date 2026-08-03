using System.Text.Json.Serialization;

namespace NovaTerminal.CommandAssist.Models;

/// <summary>
/// The deserialized shape of <c>assets/command-knowledge/command-catalogue.json</c>, the bundled
/// offline command-knowledge catalogue (V2 Phase 4b).
/// </summary>
/// <remarks>
/// <para>
/// Property names are one character on purpose. The asset holds ~2,700 examples across ~585
/// commands and ships inside the assembly; spelling <c>"description"</c> 3,300 times would cost
/// more bytes than the descriptions themselves. The C# names carry the meaning, the JSON names
/// carry the file.
/// </para>
/// <para>
/// Arrays rather than <c>IReadOnlyList</c> so the source generator has nothing to infer: this type
/// is deserialized under <c>PublishAot</c>, where reflection-based collection construction is not
/// available. See <c>CommandKnowledgeJsonContext</c>.
/// </para>
/// <para>
/// Unknown members are ignored by default, which is what lets the generator write header fields
/// (<c>generatedBy</c>) that the runtime has no use for.
/// </para>
/// </remarks>
public sealed record CommandKnowledgeCatalogue(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("license")] string? License,
    [property: JsonPropertyName("licenseUrl")] string? LicenseUrl,
    [property: JsonPropertyName("attribution")] string? Attribution,
    [property: JsonPropertyName("generatedFrom")] string? GeneratedFrom,
    [property: JsonPropertyName("entries")] CommandKnowledgeEntry[]? Entries);

/// <summary>One command's catalogue entry: what it is, and a handful of ways to run it.</summary>
/// <param name="Token">
/// The command as the user types it - <c>ssh</c>, <c>Get-ChildItem</c>, <c>git rebase</c>. Two-token
/// entries are how git's real surface is reached; see <c>CommandKnowledgeService</c> for the lookup.
/// </param>
/// <param name="Description">One line, from the tldr page's summary.</param>
/// <param name="ShellKind">
/// The shell the entry's page was written for (<c>pwsh</c>, <c>bash</c>), or <see langword="null"/>
/// for a portable tool. Derived from which tldr page directory the entry came from.
/// </param>
/// <param name="Origin">
/// <c>"nova"</c> for the handful of entries hand-authored for NovaTerminal because tldr-pages has no
/// page for them; absent for everything derived from tldr-pages. This is what keeps the CC-BY-SA
/// attribution in <see cref="CommandKnowledgeCatalogue.Attribution"/> a true statement about exactly
/// the rows it covers.
/// </param>
public sealed record CommandKnowledgeEntry(
    [property: JsonPropertyName("t")] string? Token,
    [property: JsonPropertyName("d")] string? Description,
    [property: JsonPropertyName("s")] string? ShellKind,
    [property: JsonPropertyName("o")] string? Origin,
    [property: JsonPropertyName("e")] CommandKnowledgeExample[]? Examples);

/// <summary>One example invocation and what it does.</summary>
/// <param name="Command">
/// The invocation, with tldr's <c>{{placeholder}}</c> syntax already rendered as
/// <c>&lt;placeholder&gt;</c> by the generator - the same convention the hand-written seed recipes
/// used, and the one a user reading a command line recognizes as "replace me".
/// </param>
public sealed record CommandKnowledgeExample(
    [property: JsonPropertyName("c")] string? Command,
    [property: JsonPropertyName("d")] string? Description);
