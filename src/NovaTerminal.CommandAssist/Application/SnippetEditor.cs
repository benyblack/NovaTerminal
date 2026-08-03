using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.Application;

/// <summary>
/// List / add / edit / delete over an <see cref="ISnippetStore"/>, for the Settings snippet manager
/// (V2 Phase 4b, Phase 4 task 4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists rather than the rules living in <c>SettingsWindow</c>.</strong> Until now
/// a snippet could only be created by pinning a suggestion (<c>Ctrl+Shift+S</c>) and could never be
/// renamed or deleted at all - <see cref="ISnippetStore.RemoveAsync"/> had no caller anywhere in the
/// app. The UI that fixes that is a handful of text boxes, but the rules underneath it are not
/// (what a blank name means, what a blank command means, what an edit must not silently destroy),
/// and <c>SettingsWindow</c> is an Avalonia <c>Window</c> that no test in this repo constructs. So
/// the rules live here, in the Avalonia-free assembly, where they are testable against a real store
/// on a real temp file; the window keeps the text boxes.
/// </para>
/// <para>
/// <strong>Every mutation re-reads the store.</strong> <c>JsonSnippetStore.UpsertAsync</c> re-sorts
/// on write (pinned first, then by name), so the order after an edit is the store's to decide, and a
/// locally patched list would disagree with the file the moment a rename crossed a sort boundary.
/// Re-reading also means a snippet pinned from a pane while Settings is open shows up on the next
/// edit rather than being clobbered by a stale list.
/// </para>
/// </remarks>
public sealed class SnippetEditor
{
    /// <summary>
    /// The longest name derived from a command when the user did not supply one. Long enough to
    /// tell two snippets apart at a glance, short enough not to be the command all over again.
    /// </summary>
    private const int DerivedNameMaxLength = 40;

    private readonly ISnippetStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _idFactory;
    private IReadOnlyList<CommandSnippet> _snippets = Array.Empty<CommandSnippet>();

    public SnippetEditor(ISnippetStore store, Func<DateTimeOffset>? clock = null, Func<string>? idFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _idFactory = idFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <summary>The snippets as of the last load or mutation, in the store's order.</summary>
    public IReadOnlyList<CommandSnippet> Snippets => _snippets;

    /// <summary>Reads the store. Safe to call repeatedly.</summary>
    public async Task<IReadOnlyList<CommandSnippet>> LoadAsync(CancellationToken cancellationToken = default)
    {
        _snippets = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return _snippets;
    }

    /// <summary>
    /// Creates a snippet, or returns <see langword="null"/> when there is no command to save.
    /// </summary>
    /// <remarks>
    /// A blank command is refused because a snippet with no command is a row that does nothing when
    /// the user accepts it. A blank <em>name</em> is not refused - it is derived from the command,
    /// because "I want to save this command" is the whole intent and demanding a label first is a
    /// form, not an affordance. New snippets are pinned, matching what <c>Ctrl+Shift+S</c> creates:
    /// something the user typed into a snippet manager by hand is exactly the kind of row the
    /// pinned-first ordering exists for.
    /// </remarks>
    public async Task<CommandSnippet?> AddAsync(
        string? name,
        string? commandText,
        CancellationToken cancellationToken = default)
    {
        string command = (commandText ?? string.Empty).Trim();
        if (command.Length == 0)
        {
            return null;
        }

        var snippet = new CommandSnippet(
            Id: _idFactory(),
            Name: ResolveName(name, command),
            CommandText: command,
            Description: null,
            ShellKind: null,
            WorkingDirectory: null,
            IsPinned: true,
            CreatedAt: _clock(),
            LastUsedAt: null);

        await _store.UpsertAsync(snippet, cancellationToken).ConfigureAwait(false);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return snippet;
    }

    /// <summary>
    /// Renames a snippet and/or rewrites its command. Returns <see langword="false"/> when the id is
    /// unknown or the new command is blank.
    /// </summary>
    /// <remarks>
    /// Everything the editor does not show is carried across unchanged - description, shell kind,
    /// working directory, pinned state, creation and last-used timestamps. A two-field editor that
    /// wrote a whole record would quietly discard the cwd a pinned suggestion was captured with, and
    /// the loss would only show up later as a snippet that stopped ranking where it used to.
    /// </remarks>
    public async Task<bool> UpdateAsync(
        string snippetId,
        string? name,
        string? commandText,
        CancellationToken cancellationToken = default)
    {
        CommandSnippet? existing = Find(snippetId);
        if (existing == null)
        {
            return false;
        }

        string command = (commandText ?? string.Empty).Trim();
        if (command.Length == 0)
        {
            return false;
        }

        await _store.UpsertAsync(
            existing with
            {
                Name = ResolveName(name, command),
                CommandText = command,
            },
            cancellationToken).ConfigureAwait(false);

        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Deletes a snippet. Returns <see langword="false"/> when the id is unknown.</summary>
    public async Task<bool> RemoveAsync(string snippetId, CancellationToken cancellationToken = default)
    {
        if (Find(snippetId) == null)
        {
            return false;
        }

        await _store.RemoveAsync(snippetId, cancellationToken).ConfigureAwait(false);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private CommandSnippet? Find(string snippetId)
    {
        if (string.IsNullOrWhiteSpace(snippetId))
        {
            return null;
        }

        foreach (CommandSnippet snippet in _snippets)
        {
            if (string.Equals(snippet.Id, snippetId, StringComparison.Ordinal))
            {
                return snippet;
            }
        }

        return null;
    }

    /// <summary>The user's name, or the first line of the command truncated to something readable.</summary>
    private static string ResolveName(string? name, string command)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > 0)
        {
            return trimmed;
        }

        // First line only: a pasted multi-line command would otherwise put a newline in a label that
        // is rendered on one line everywhere it appears.
        int lineBreak = command.IndexOfAny(['\r', '\n']);
        string firstLine = lineBreak >= 0 ? command[..lineBreak] : command;

        return firstLine.Length <= DerivedNameMaxLength
            ? firstLine
            : firstLine[..DerivedNameMaxLength].TrimEnd() + "...";
    }
}
