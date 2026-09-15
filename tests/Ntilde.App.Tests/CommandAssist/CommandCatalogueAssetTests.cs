using System.Text.Json;
using NovaTerminal.CommandAssist.Domain;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.Storage;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// Invariants of the generated catalogue asset (V2 Phase 4b, Phase 4 task 3).
/// </summary>
/// <remarks>
/// <para>
/// These are assertions about <em>the committed file</em>, not about the generator: the generator is
/// a PowerShell script run by hand against a tldr-pages checkout, so it is not on the build path and
/// nothing but this suite stands between a bad regeneration and a shipped catalogue. Every rule the
/// script enforces at generation time is re-checked here against the artefact, because the artefact
/// is what the user gets.
/// </para>
/// <para>
/// Read out of the embedded resource rather than off disk, so the test also proves the asset is
/// actually embedded under the logical name the service looks for - the failure mode where the file
/// is committed, correct, and invisible to the running app.
/// </para>
/// </remarks>
public sealed class CommandCatalogueAssetTests
{
    /// <summary>The plan's floor.</summary>
    private const int MinimumCommandCount = 200;

    /// <summary>The design doc's budget.</summary>
    private const int MaximumBytes = 2 * 1024 * 1024;

    /// <summary>The generator's cap. One is the floor; see the script header for why not three.</summary>
    private const int MaximumExamplesPerEntry = 6;

    private static readonly Lazy<byte[]> RawAsset = new(ReadEmbeddedAsset);
    private static readonly Lazy<CommandKnowledgeCatalogue> Catalogue = new(ParseAsset);

    [Fact]
    public void Asset_is_embedded_under_the_name_the_service_looks_for()
    {
        Assert.NotEmpty(RawAsset.Value);
    }

    [Fact]
    public void Asset_stays_within_the_size_budget()
    {
        Assert.InRange(RawAsset.Value.Length, 1, MaximumBytes);
    }

    [Fact]
    public void Catalogue_covers_at_least_the_planned_number_of_commands()
    {
        Assert.True(
            Catalogue.Value.Entries!.Length >= MinimumCommandCount,
            $"Catalogue has {Catalogue.Value.Entries!.Length} commands; the floor is {MinimumCommandCount}.");
    }

    [Fact]
    public void Every_entry_has_a_usable_token_and_description()
    {
        foreach (CommandKnowledgeEntry entry in Catalogue.Value.Entries!)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Token), "An entry has no token.");
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Description),
                $"Entry '{entry.Token}' has no description.");

            // A token is what the user types. Leftover tldr placeholder syntax, a markdown artefact
            // or a newline in there means the parser mis-read a page and the entry is unreachable.
            Assert.DoesNotContain("{{", entry.Token!, StringComparison.Ordinal);
            Assert.DoesNotContain("`", entry.Token!, StringComparison.Ordinal);
            Assert.Equal(entry.Token!.Trim(), entry.Token);
            Assert.DoesNotContain('\n', entry.Token!);
        }
    }

    [Fact]
    public void Every_entry_has_between_one_and_six_non_empty_examples()
    {
        foreach (CommandKnowledgeEntry entry in Catalogue.Value.Entries!)
        {
            Assert.NotNull(entry.Examples);
            Assert.InRange(entry.Examples!.Length, 1, MaximumExamplesPerEntry);

            foreach (CommandKnowledgeExample example in entry.Examples)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(example.Command),
                    $"Entry '{entry.Token}' has an example with no command.");
                Assert.False(
                    string.IsNullOrWhiteSpace(example.Description),
                    $"Entry '{entry.Token}' has an example with no description.");

                // The generator renders {{placeholder}} as <placeholder>. An unrendered one would be
                // inserted onto the user's command line verbatim.
                Assert.DoesNotContain("{{", example.Command!, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Tokens_are_unique_ignoring_case()
    {
        // Lookup is case-insensitive, so two entries differing only in case are one reachable entry
        // and one dead one - and which of them wins would depend on file order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CommandKnowledgeEntry entry in Catalogue.Value.Entries!)
        {
            Assert.True(seen.Add(entry.Token!), $"Duplicate catalogue token '{entry.Token}'.");
        }
    }

    [Fact]
    public void Attribution_names_the_source_and_the_licence()
    {
        // The CC-BY-SA obligation is on this string: it is what the Help popup footer shows.
        string attribution = Catalogue.Value.Attribution ?? string.Empty;
        Assert.Contains("tldr-pages", attribution, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CC BY-SA", attribution, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("CC-BY-SA-4.0", Catalogue.Value.License);
    }

    [Fact]
    public void Entries_not_derived_from_tldr_are_marked_as_such()
    {
        // The attribution says entries marked "o": "nova" are not tldr content. That claim is only
        // true if the mark is actually on them - and the supplement is the only source of them.
        CommandKnowledgeEntry[] marked = Catalogue.Value.Entries!
            .Where(entry => entry.Origin != null)
            .ToArray();

        Assert.NotEmpty(marked);
        Assert.All(marked, entry => Assert.Equal("nova", entry.Origin));
        Assert.Contains(marked, entry => entry.Token == "Get-Process");
    }

    /// <summary>
    /// The commands the V2 design doc named as misses of the seven-command seed providers. If a
    /// regeneration drops one of these, #250 has quietly reopened.
    /// </summary>
    [Theory]
    [InlineData("ssh")]
    [InlineData("curl")]
    [InlineData("tar")]
    [InlineData("find")]
    [InlineData("kubectl")]
    [InlineData("rg")]
    [InlineData("systemctl")]
    [InlineData("journalctl")]
    [InlineData("dotnet")]
    [InlineData("cargo")]
    [InlineData("Get-Process")]
    [InlineData("Select-String")]
    [InlineData("Get-Content")]
    [InlineData("Test-NetConnection")]
    public void Catalogue_covers_the_commands_the_design_doc_named(string token)
    {
        Assert.Contains(
            Catalogue.Value.Entries!,
            entry => string.Equals(entry.Token, token, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Catalogue_carries_git_subcommands_as_two_token_entries()
    {
        CommandKnowledgeEntry[] subcommands = Catalogue.Value.Entries!
            .Where(entry => entry.Token!.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(subcommands.Length >= 20, $"Only {subcommands.Length} git subcommand entries.");
        Assert.Contains(subcommands, entry => entry.Token == "git rebase");
        Assert.Contains(subcommands, entry => entry.Token == "git stash");
    }

    /// <summary>
    /// The full set of examples in the current asset where '[' and ']' legitimately do not balance,
    /// verified by scanning every example's raw bracket count (not just the ones that looked
    /// suspicious by eye): <c>printf</c>'s ANSI CSI escapes (<c>\e[1;34m</c>, <c>\e[0m</c>) open with
    /// <c>\e[</c> and close with a letter, never <c>]</c>, so they are orphaned by construction; and
    /// <c>telnet</c>'s <c>&lt;Ctrl ]&gt;</c>, which names the literal escape-character keystroke and
    /// is not a bracket pair at all. Both predate this test and are unrelated to the tar
    /// suffix-alternation bug the test below exists to catch. Exact-string rather than pattern-based
    /// so a regeneration that introduces a *new* unbalanced example still fails loudly instead of
    /// being silently swallowed by a broad "anything with an escape code" rule.
    /// </summary>
    private static readonly HashSet<string> KnownUnbalancedBracketExamples = new(StringComparer.Ordinal)
    {
        "printf \"<\\e[1;34m%.3d\\e[0m\\n>\" <42>",
        "<Ctrl ]>",
    };

    [Fact]
    public void Example_commands_have_balanced_square_brackets()
    {
        // The tldr suffix-alternation idiom (`{{path/to/source.tar[.gz|.bz2|.xz]}}`) used to defeat
        // the generic `|`-splitting in Format-ExampleCommand: it split the placeholder body at the
        // first alternative's own bracket, leaving an orphaned `[` and a truncated value on the
        // user's command line (`tar xvf path/to/source.tar[.gz`, never wrapped in <...>). Every other
        // bracket usage that survives into a rendered example - jq/yq's `.[0]`, `tr "[:lower:]"`,
        // `[System.Convert]`, `grep <[s]tring>`, a `[0-9]` character class, and the two entries in
        // KnownUnbalancedBracketExamples above - opens and closes within the same example or is an
        // explicitly-named exception, so a plain '[' vs ']' count (minus those two) is the guard: an
        // unlisted example with more '[' than ']' or vice versa is the bug again.
        foreach (CommandKnowledgeEntry entry in Catalogue.Value.Entries!)
        {
            foreach (CommandKnowledgeExample example in entry.Examples!)
            {
                string command = example.Command!;
                if (KnownUnbalancedBracketExamples.Contains(command))
                {
                    continue;
                }

                int opens = command.Count(c => c == '[');
                int closes = command.Count(c => c == ']');
                Assert.True(
                    opens == closes,
                    $"Entry '{entry.Token}' has an unbalanced-bracket example: \"{command}\" " +
                    $"({opens} '[' vs {closes} ']').");
            }
        }
    }

    [Fact]
    public void Tar_entries_render_the_suffix_alternation_idiom_without_leftover_brackets()
    {
        // Pins the fixed idiom directly against the one page in the curated set that exercises it:
        // tldr's tar.md writes `{{path/to/source.tar[.gz|.bz2|.xz]}}` for both its extract examples.
        CommandKnowledgeEntry tar = Catalogue.Value.Entries!.Single(e => e.Token == "tar");

        Assert.All(tar.Examples!, example => Assert.DoesNotContain('[', example.Command!));
        Assert.Contains(tar.Examples!, example => example.Command == "tar xvf <path/to/source.tar.gz>");
        Assert.Contains(
            tar.Examples!,
            example => example.Command == "tar xf <path/to/source.tar.gz> --directory <path/to/directory>");
    }

    [Fact]
    public void Powershell_entries_carry_the_pwsh_shell_hint()
    {
        // The shell hint is what carries the seed provider's "prefer shell-specific" intent into the
        // catalogue: a cmdlet entry says which shell it belongs to, so the row can too.
        CommandKnowledgeEntry entry = Catalogue.Value.Entries!
            .Single(x => x.Token == "Get-ChildItem");

        Assert.Equal("pwsh", entry.ShellKind);
    }

    private static byte[] ReadEmbeddedAsset()
    {
        using Stream? stream = typeof(CommandKnowledgeService).Assembly
            .GetManifestResourceStream(CommandKnowledgeService.CatalogueResourceName);
        Assert.NotNull(stream);

        using var buffer = new MemoryStream();
        stream!.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static CommandKnowledgeCatalogue ParseAsset()
    {
        CommandKnowledgeCatalogue? catalogue = JsonSerializer.Deserialize(
            RawAsset.Value,
            CommandKnowledgeJsonContext.Default.CommandKnowledgeCatalogue);

        Assert.NotNull(catalogue);
        Assert.NotNull(catalogue!.Entries);
        return catalogue;
    }
}
