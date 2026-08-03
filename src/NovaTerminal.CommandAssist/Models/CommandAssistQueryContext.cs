namespace NovaTerminal.CommandAssist.Models;

/// <param name="HostId">
/// The remote host this pane is connected to, or <see langword="null"/> for a local pane. Together
/// with <paramref name="IsRemote"/> it is what makes a history entry "from here": V2 Phase 3a ranks
/// entries that share the current pane's host (or its localness) above the rest, which is the fix for
/// the owner's report that <c>Ctrl+R</c> mixed every session's commands together. Declared last so
/// that adding it could not silently re-bind an existing positional call site.
/// </param>
public sealed record CommandAssistQueryContext(
    string Input,
    string? WorkingDirectory,
    string? ShellKind,
    string? ProfileId,
    bool IsRemote = false,
    bool IncludeHistorySuggestions = true,
    bool IncludeSnippetSuggestions = true,
    bool IncludePathSuggestions = true,
    string? HostId = null);
