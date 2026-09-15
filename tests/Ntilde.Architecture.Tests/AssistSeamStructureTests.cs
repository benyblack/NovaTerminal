using System.IO;
using System.Linq;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Structural guards for the V2 Phase 5 AI content-provider seam.
/// </summary>
/// <remarks>
/// <para>
/// The seam's one load-bearing promise is that nothing unredacted reaches a content provider.
/// <c>RedactedText</c> enforces the type half of that at compile time - there is no public way to
/// make one without an <c>ISecretsFilter</c> in hand. These tests enforce the other half: that there
/// is exactly <em>one</em> place where a request is built, so the promise can be audited by reading
/// one file rather than by trusting that every future construction site remembered.
/// </para>
/// <para>
/// A source scan rather than a Roslyn analyzer, deliberately. The thing being asserted is "one file
/// contains this", which is what a reader checks and what a reviewer checks; a source scan says so
/// directly, in twenty lines, with a failure message that names the offending file.
/// </para>
/// </remarks>
public class AssistSeamStructureTests
{
    private const string AssistSourceRoot = "src/NovaTerminal.CommandAssist";

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NovaTerminal.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private static string[] AssistSourceFiles()
        => Directory.GetFiles(Path.Combine(RepoRoot(), AssistSourceRoot), "*.cs", SearchOption.AllDirectories);

    /// <summary>
    /// Every <c>AssistContentRequest</c> in existence is built by <c>AssistContentRequestFactory</c>,
    /// which redacts every string it is given.
    /// </summary>
    /// <remarks>
    /// The record's constructor is <c>internal</c>, so this only has to hold within the assist
    /// assembly - no other assembly can construct one at all. Adding a second site inside it is the
    /// realistic regression: a Fix path that "just needs one more field" and copies the shape rather
    /// than extending the factory.
    /// </remarks>
    [Fact]
    public void AssistContentRequest_is_constructed_in_exactly_one_file()
    {
        string[] offenders = AssistSourceFiles()
            .Where(path => File.ReadAllText(path).Contains("new AssistContentRequest(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, "AssistContentRequestFactory.cs", StringComparison.Ordinal))
            .ToArray()!;

        Assert.True(offenders.Length == 0,
            "AssistContentRequest may only be constructed in AssistContentRequestFactory, which is " +
            "where SecretsFilter runs. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// <c>RedactedText</c> is only minted by the request factory, which is the only caller that has
    /// run the filter in the same breath.
    /// </summary>
    /// <remarks>
    /// <c>RedactedText.Redact</c> takes an <c>ISecretsFilter</c>, so a second caller could not skip
    /// redaction even if it wanted to - but it could redact with a <em>different</em> filter than the
    /// one the capture pipeline persists history with, and "what is redacted before it is stored" and
    /// "what is redacted before it crosses the seam" drifting apart is a bug nobody would find by
    /// reading either half.
    /// </remarks>
    [Fact]
    public void RedactedText_is_minted_in_exactly_one_file()
    {
        string[] offenders = AssistSourceFiles()
            .Where(path =>
            {
                string text = File.ReadAllText(path);
                return text.Contains("RedactedText.Redact(", StringComparison.Ordinal) ||
                       text.Contains("RedactedText.RedactOptional(", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .Where(name =>
                !string.Equals(name, "AssistContentRequestFactory.cs", StringComparison.Ordinal) &&
                !string.Equals(name, "RedactedText.cs", StringComparison.Ordinal))
            .ToArray()!;

        Assert.True(offenders.Length == 0,
            "RedactedText may only be minted in AssistContentRequestFactory. Offenders: " +
            string.Join(", ", offenders));
    }
}
