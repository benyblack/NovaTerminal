using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.Storage;

namespace NovaTerminal.CommandAssist.Domain;

/// <summary>
/// The command knowledge catalogue: Help docs and Recipe rows for ~585 commands, served out of the
/// offline asset bundled with the app (V2 Phase 4b, Phase 4 task 3; closes #250).
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>LocalCommandDocsProvider</c> (7 commands, 9 hand-written help items) and
/// <c>SeedRecipeProvider</c> (7 recipes). Those covered <c>git</c>, <c>docker</c>, <c>ls</c>,
/// <c>cd</c>, <c>grep</c>, <c>Get-ChildItem</c> and <c>Set-Location</c>; every other command in the
/// world got "No local help found", which is the shape of #250.
/// </para>
/// <para>
/// <strong>Ordered sources.</strong> The design doc's Pillar 5 lists three: (a) the bundled
/// catalogue, (b) local probing for an "open full help" action, (c) the Phase 5 AI seam. (a) and (b)
/// are here. (c) is deliberately absent rather than stubbed - Phase 5 adapts this class to
/// <c>IAssistContentProvider</c>, and an empty interface implemented early is a guess about a design
/// that has not been made.
/// </para>
/// <para>
/// <strong>Docs versus recipes.</strong> A catalogue entry is one summary line and up to six example
/// invocations. The summary is the Doc row: it answers "what is this command". The examples are the
/// Recipe rows: they answer "how do I run it", and each one is insertable, which is why the row's
/// display text *is* the command rather than a prose title. The seed provider wrote titles like
/// "Clone and switch" over a command the user could not see; a row whose text is the command it
/// inserts cannot mislead about what pressing Enter will do.
/// </para>
/// <para>
/// <strong>Loading is lazy and off the caller's thread.</strong> The asset is ~825 KB of JSON. It is
/// parsed on first use inside a <see cref="Task.Run"/>, never during construction: this type is
/// built at the App composition root on the startup path, and the plan's startup budget does not
/// have a JSON parse in it. Both public methods are async, so there is nowhere for the cost to leak
/// onto the UI thread.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> One <see cref="Lazy{T}"/> with
/// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>, so concurrent panes opening Help at
/// the same moment parse once. A parse that throws is cached as an empty catalogue rather than
/// retried per call - a corrupt embedded resource is not a transient fault, and retrying it once per
/// keystroke would turn a broken build into a hang.
/// </para>
/// </remarks>
public sealed class CommandKnowledgeService : ICommandDocsProvider, IRecipeProvider, ICommandKnowledgeAttributionSource
{
    /// <summary>
    /// The embedded asset's logical name, pinned in <c>NovaTerminal.CommandAssist.csproj</c>.
    /// </summary>
    internal const string CatalogueResourceName =
        "NovaTerminal.CommandAssist.CommandKnowledge.command-catalogue.json";

    /// <summary>
    /// Command names that are not the command - an elevation or environment wrapper the real token
    /// follows. Stripped so that Help on <c>sudo systemctl status nginx</c> answers about
    /// <c>systemctl status</c> rather than about <c>sudo</c>.
    /// </summary>
    private static readonly string[] WrapperTokens = ["sudo", "doas", "command", "exec", "time", "env"];

    private readonly ICommandHelpProbe? _probe;
    private readonly Lazy<LoadedCatalogue> _catalogue;

    public CommandKnowledgeService(ICommandHelpProbe? probe = null)
        : this(probe, LoadEmbeddedCatalogue)
    {
    }

    /// <summary>Test seam: lets a test supply a catalogue without an embedded resource.</summary>
    internal CommandKnowledgeService(ICommandHelpProbe? probe, Func<CommandKnowledgeCatalogue?> catalogueFactory)
    {
        _probe = probe;
        _catalogue = new Lazy<LoadedCatalogue>(
            () => LoadedCatalogue.Build(catalogueFactory()),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The catalogue's licence line, for display wherever its content is shown. Null until the
    /// catalogue has been loaded, which the Help path always does before it reads this.
    /// </summary>
    public string? Attribution => _catalogue.IsValueCreated ? _catalogue.Value.Attribution : null;

    /// <summary>How many commands the loaded catalogue holds. Diagnostics and tests.</summary>
    internal int Count => _catalogue.Value.Entries.Count;

    public async Task<IReadOnlyList<CommandHelpItem>> GetHelpAsync(
        CommandHelpQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        LoadedCatalogue catalogue = await LoadAsync(cancellationToken).ConfigureAwait(false);
        CommandKnowledgeEntry? entry = Resolve(catalogue, query);
        if (entry == null || string.IsNullOrWhiteSpace(entry.Description))
        {
            return Array.Empty<CommandHelpItem>();
        }

        return
        [
            new CommandHelpItem(
                Title: entry.Token!,
                Command: entry.Token!,
                Description: entry.Description,
                ShellKind: entry.ShellKind,
                Badges: ["Doc"])
        ];
    }

    public async Task<IReadOnlyList<CommandHelpItem>> GetRecipesAsync(
        CommandHelpQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        LoadedCatalogue catalogue = await LoadAsync(cancellationToken).ConfigureAwait(false);
        CommandKnowledgeEntry? entry = Resolve(catalogue, query);

        var items = new List<CommandHelpItem>();

        if (entry?.Examples != null)
        {
            foreach (CommandKnowledgeExample example in entry.Examples)
            {
                if (string.IsNullOrWhiteSpace(example.Command))
                {
                    continue;
                }

                items.Add(new CommandHelpItem(
                    Title: example.Command!,
                    Command: example.Command!,
                    Description: example.Description,
                    ShellKind: entry.ShellKind,
                    Badges: ["Recipe"]));
            }
        }

        // The probe row is appended even when the catalogue knows nothing, and that is the point of
        // having two sources: a command the catalogue has never heard of still has `--help` on the
        // machine in front of the user, and "here is how to read its real docs" beats "No local help
        // found". It goes last because a curated example is a better answer than a manual page when
        // both exist.
        CommandHelpProbeResult? probed = TryProbe(entry?.Token ?? ResolveProbeToken(query), query.ShellKind);
        if (probed.HasValue)
        {
            items.Add(new CommandHelpItem(
                Title: $"Open full help: {probed.Value.Command}",
                Command: probed.Value.Command,
                Description: probed.Value.Description,
                ShellKind: query.ShellKind,
                Badges: ["Help"]));
        }

        return items;
    }

    private CommandHelpProbeResult? TryProbe(string? token, string? shellKind)
    {
        if (_probe == null || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            return _probe.Probe(token!, shellKind);
        }
        catch
        {
            // The probe reaches across into PATH and the filesystem. A probe that throws found
            // nothing, which is the same answer as a probe that found nothing - and losing the
            // catalogue rows because the "extra" row failed would be the wrong trade.
            return null;
        }
    }

    private Task<LoadedCatalogue> LoadAsync(CancellationToken cancellationToken)
    {
        if (_catalogue.IsValueCreated)
        {
            return Task.FromResult(_catalogue.Value);
        }

        return Task.Run(() => _catalogue.Value, cancellationToken);
    }

    /// <summary>
    /// Finds the catalogue entry a Help request is about, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two-token first.</strong> <c>git</c> alone is a catalogue entry with six generic
    /// examples; <c>git rebase</c> is the question a user actually has, and the catalogue carries
    /// ~200 of those. So the two-token key is tried before the one-token key, and only demoted when
    /// the second word is an option (<c>git --version</c>) or absent.
    /// </para>
    /// <para>
    /// <strong>Normalization, and what it does not do.</strong> Lookup is
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> over whitespace-collapsed keys, which is what
    /// makes <c>get-childitem</c>, <c>Get-ChildItem</c> and <c>GET-CHILDITEM</c> the same entry -
    /// PowerShell is case-insensitive and a user typing at speed will not shift-case a cmdlet. A path
    /// (<c>/usr/bin/ssh</c>, <c>.\git.exe</c>) is reduced to its file name and a Windows executable
    /// suffix is dropped, because those are spellings of the same command. What it deliberately does
    /// *not* do is fuzzy-match: an entry the user did not ask for is worse than no entry, and the
    /// nearest-command guessing that Fix mode does is Fix mode's job, done against a failure the user
    /// has already seen.
    /// </para>
    /// <para>
    /// <strong>Where the tokens come from.</strong> The query text first, the selection second, and
    /// <see cref="CommandHelpQuery.CommandToken"/> last. The first two are raw enough to see a second
    /// word; <c>CommandToken</c> has already been reduced to one by <c>RecognizedCommandParser</c>,
    /// so consulting it first would make every two-token lookup impossible.
    /// </para>
    /// </remarks>
    private static CommandKnowledgeEntry? Resolve(LoadedCatalogue catalogue, CommandHelpQuery query)
    {
        foreach (string key in BuildLookupKeys(query))
        {
            if (catalogue.Entries.TryGetValue(key, out CommandKnowledgeEntry? entry))
            {
                return entry;
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildLookupKeys(CommandHelpQuery query)
    {
        var keys = new List<string>(3);

        foreach (string source in new[] { query.RawInput, query.SelectedText ?? string.Empty })
        {
            TokenScan scan = SplitTokens(source);
            if (scan.Tokens.Length == 0)
            {
                continue;
            }

            // Normally the command is the first word and nothing else is a candidate. After a
            // wrapper was stripped it is not: `sudo -u www tar -xzf a.tgz` has the command at index
            // two, behind an option that takes a value, and no amount of dash-counting can tell
            // `www` (a value) from `tar` (the command) without knowing sudo's option table. So a
            // stripped wrapper widens the search to the next few non-option words and lets the
            // catalogue arbitrate: `www` is not in it and `tar` is. The window is small because the
            // widening is a guess, and a guess that reaches the fifth word of a line is a guess
            // about a command that is not there.
            int candidatePositions = scan.WrapperStripped ? Math.Min(scan.Tokens.Length, 4) : 1;

            for (int i = 0; i < candidatePositions; i++)
            {
                string primary = NormalizeToken(scan.Tokens[i]);
                if (primary.Length == 0 || primary.StartsWith('-'))
                {
                    continue;
                }

                if (i + 1 < scan.Tokens.Length)
                {
                    string secondary = NormalizeToken(scan.Tokens[i + 1]);
                    if (secondary.Length > 0 && !secondary.StartsWith('-'))
                    {
                        AddKey(keys, primary + " " + secondary);
                    }
                }

                AddKey(keys, primary);
            }
        }

        if (!string.IsNullOrWhiteSpace(query.CommandToken))
        {
            AddKey(keys, NormalizeToken(query.CommandToken!));
        }

        return keys;
    }

    private static void AddKey(List<string> keys, string key)
    {
        if (key.Length > 0 && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            keys.Add(key);
        }
    }

    /// <summary>The token the probe is asked about when the catalogue knows nothing.</summary>
    private static string? ResolveProbeToken(CommandHelpQuery query)
    {
        foreach (string source in new[] { query.RawInput, query.SelectedText ?? string.Empty })
        {
            foreach (string token in SplitTokens(source).Tokens)
            {
                string normalized = NormalizeToken(token);
                if (normalized.Length > 0 && !normalized.StartsWith('-'))
                {
                    return normalized;
                }
            }
        }

        return string.IsNullOrWhiteSpace(query.CommandToken)
            ? null
            : NormalizeToken(query.CommandToken!);
    }

    /// <summary>The words of a command line, with any leading wrapper removed.</summary>
    private readonly record struct TokenScan(string[] Tokens, bool WrapperStripped);

    private static TokenScan SplitTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TokenScan([], false);
        }

        string[] tokens = text!.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // A wrapper is not the command. Stripped repeatedly so `sudo time make` reaches `make`, but
        // never past the last token, so a bare `sudo` still resolves to the sudo entry - which is
        // the right answer when `sudo` is all the user has typed.
        int start = 0;
        while (start < tokens.Length - 1 &&
               WrapperTokens.Contains(NormalizeToken(tokens[start]), StringComparer.OrdinalIgnoreCase))
        {
            start++;
        }

        return start == 0 ? new TokenScan(tokens, false) : new TokenScan(tokens[start..], true);
    }

    /// <summary>
    /// Reduces one raw word to the form the catalogue is keyed on: no quotes, no directory, no
    /// Windows executable suffix.
    /// </summary>
    private static string NormalizeToken(string token)
    {
        string trimmed = token.Trim().Trim('"', '\'');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        int lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0 && lastSeparator < trimmed.Length - 1)
        {
            trimmed = trimmed[(lastSeparator + 1)..];
        }

        foreach (string suffix in new[] { ".exe", ".cmd", ".bat", ".ps1" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length];
                break;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Reads and parses the embedded catalogue. Returns <see langword="null"/> on any failure; the
    /// caller turns that into an empty catalogue.
    /// </summary>
    /// <remarks>
    /// A missing resource throws in <c>Debug</c> and degrades in <c>Release</c>: in a developer's
    /// build a catalogue that failed to embed is a build mistake worth surfacing immediately, and in
    /// a user's build it is a Help popup that says "no local help" instead of an app that will not
    /// open one.
    /// </remarks>
    private static CommandKnowledgeCatalogue? LoadEmbeddedCatalogue()
    {
        try
        {
            using Stream? stream = typeof(CommandKnowledgeService).Assembly
                .GetManifestResourceStream(CatalogueResourceName);
            if (stream == null)
            {
#if DEBUG
                throw new InvalidOperationException(
                    $"Embedded command catalogue '{CatalogueResourceName}' is missing. It is embedded " +
                    "from assets/command-knowledge/ by NovaTerminal.CommandAssist.csproj and generated " +
                    "by scripts/generate-command-catalogue.ps1.");
#else
                return null;
#endif
            }

            return JsonSerializer.Deserialize(stream, CommandKnowledgeJsonContext.Default.CommandKnowledgeCatalogue);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The catalogue in the shape lookups want: a keyed index plus the licence line.</summary>
    private sealed class LoadedCatalogue
    {
        private LoadedCatalogue(
            IReadOnlyDictionary<string, CommandKnowledgeEntry> entries,
            string? attribution)
        {
            Entries = entries;
            Attribution = attribution;
        }

        public IReadOnlyDictionary<string, CommandKnowledgeEntry> Entries { get; }

        public string? Attribution { get; }

        public static LoadedCatalogue Build(CommandKnowledgeCatalogue? catalogue)
        {
            var index = new Dictionary<string, CommandKnowledgeEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (CommandKnowledgeEntry entry in catalogue?.Entries ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Token))
                {
                    continue;
                }

                // Whitespace-collapsed, so `git   rebase` read off a grid with an odd repaint keys
                // the same entry as `git rebase`. Case is handled by the comparer.
                string key = string.Join(
                    ' ',
                    entry.Token!.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

                // First wins. The generator emits one entry per token, so a duplicate means a
                // hand-edited asset, and silently preferring the later one would hide that.
                if (key.Length > 0)
                {
                    index.TryAdd(key, entry);
                }
            }

            return new LoadedCatalogue(index, catalogue?.Attribution);
        }
    }
}

/// <summary>
/// Exposes the bundled catalogue's licence line so a surface showing its content can credit it.
/// </summary>
/// <remarks>
/// <para>
/// The tldr-pages content is CC-BY-SA 4.0, which requires attribution wherever it is used. The app
/// has no About dialog, so the credit goes where the content goes: the Command Assist popup footer,
/// visible whenever Help is open. This interface is what lets the controller ask for the line
/// without knowing that its docs provider is the catalogue - a Phase 5 provider chain that does not
/// serve tldr content simply does not implement it, and the footer stays empty.
/// </para>
/// <para>
/// A property rather than a constant because the line is the asset's own
/// <c>attribution</c> field: the generator writes it, so a regeneration that changes the licensing
/// story changes the credit with it, and the two cannot drift.
/// </para>
/// </remarks>
public interface ICommandKnowledgeAttributionSource
{
    /// <summary>
    /// The licence and attribution line, or <see langword="null"/> when the catalogue has not been
    /// loaded yet or carries none.
    /// </summary>
    string? Attribution { get; }
}
