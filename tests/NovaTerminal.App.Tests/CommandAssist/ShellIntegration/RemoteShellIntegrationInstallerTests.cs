using System.Text;
using System.Text.RegularExpressions;
using NovaTerminal.CommandAssist.ShellIntegration.Remote;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration;

/// <summary>
/// The one-line installer Settings copies (see
/// docs/plans/2026-08-06-remote-shell-integration-installer-design.md).
/// </summary>
/// <remarks>
/// These are static assertions on the generated command. They exist to pin the three properties the
/// design rests on and that nothing else can catch: it is one line (two lines would be two history
/// entries, which is the whole point of the change), the payload is pure base64 (which is why no
/// escaping logic exists anywhere in this path), and the snippet survives the compress/encode round
/// trip byte-for-byte. RemoteInstallerIntegrationTests is the layer that says it *works*.
/// </remarks>
public sealed class RemoteShellIntegrationInstallerTests
{
    [Fact]
    public void BashOrZshInstaller_IsExactlyOneLine()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.BashOrZsh);

        Assert.DoesNotContain("\n", command, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The single-quoted payload is the reason this design has no escaping logic: base64's alphabet
    /// contains no shell metacharacter, so the quoting can never be wrong. If an encoding change
    /// ever put a quote or a backslash in there, every one-liner would break at the paste.
    /// </summary>
    [Fact]
    public void BashOrZshInstaller_CarriesPureBase64InSingleQuotes()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.BashOrZsh);

        Match match = Regex.Match(command, @"printf %s '([^']*)'");

        Assert.True(match.Success, $"no single-quoted printf payload in: {command}");
        Assert.Matches("^[A-Za-z0-9+/=]+$", match.Groups[1].Value);
    }

    [Fact]
    public void BashOrZshInstaller_PayloadDecodesToTheInstallerScript()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.BashOrZsh);
        string payload = Regex.Match(command, @"printf %s '([^']*)'").Groups[1].Value;

        string decoded = Decompress(payload);

        Assert.Equal(
            RemoteShellIntegrationSnippets.BuildInstallerScript(
                RemoteShellIntegrationShell.BashOrZsh),
            decoded);
    }

    /// <summary>
    /// The snippet has to arrive on the remote host unchanged; a heredoc that mangled it would still
    /// produce a plausible-looking installer.
    /// </summary>
    [Fact]
    public void BashOrZshInstaller_EmbedsTheSnippetByteForByte()
    {
        string installer = RemoteShellIntegrationSnippets.BuildInstallerScript(
            RemoteShellIntegrationShell.BashOrZsh);
        string snippet = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains(snippet.TrimEnd('\n'), installer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The installer's loader line and the descriptor's must be the same string, or the row tells
    /// the user one thing and the installer writes another.
    /// </summary>
    [Fact]
    public void BashOrZshInstaller_UsesTheDescriptorsLoaderLine()
    {
        string installer = RemoteShellIntegrationSnippets.BuildInstallerScript(
            RemoteShellIntegrationShell.BashOrZsh);
        string? loader = RemoteShellIntegrationSnippets.GetLoaderLine(
            RemoteShellIntegrationShell.BashOrZsh);

        Assert.NotNull(loader);
        Assert.Contains(loader, installer, StringComparison.Ordinal);
    }

    /// <summary>
    /// A snippet line starting with the heredoc terminator would end the heredoc early, so the
    /// installed file would be silently truncated and the rest of the snippet would run as shell
    /// commands on the user's remote host. Failing at copy time is the only place this can be caught.
    /// </summary>
    [Fact]
    public void BuildInstallerScript_ThrowsWhenTheSnippetCollidesWithTheDelimiter()
    {
        string colliding = "echo one\n__NOVA_SNIPPET_EOF__\necho two\n";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RemoteShellIntegrationSnippets.BuildInstallerScript(
                RemoteShellIntegrationShell.BashOrZsh,
                colliding));

        Assert.Contains("__NOVA_SNIPPET_EOF__", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>And no shipped snippet collides, which is why the guard never fires in practice.</summary>
    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh)]
    [InlineData(RemoteShellIntegrationShell.Fish)]
    public void NoSnippet_CollidesWithTheInstallerDelimiter(RemoteShellIntegrationShell shell)
    {
        string installer = RemoteShellIntegrationSnippets.BuildInstallerScript(shell);
        string snippet = RemoteShellIntegrationSnippets.Read(shell);

        Assert.Contains("__NOVA_SNIPPET_EOF__", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("__NOVA_SNIPPET_EOF__", snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void FishInstaller_IsExactlyOneLine()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.Fish);

        Assert.DoesNotContain("\n", command, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// fish syntax, not sh: <c>set -l</c> and <c>(mktemp)</c> rather than <c>$(mktemp)</c>. Pasting
    /// an sh one-liner into fish fails on the first command substitution.
    /// </summary>
    [Fact]
    public void FishInstaller_UsesFishSyntax()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.Fish);

        Assert.Contains("set -l __nova_t (mktemp)", command, StringComparison.Ordinal);
        Assert.Contains("set -e __nova_t", command, StringComparison.Ordinal);
        Assert.DoesNotContain("$(mktemp)", command, StringComparison.Ordinal);
    }

    [Fact]
    public void FishInstaller_PayloadDecodesToTheInstallerScript()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.Fish);
        string payload = Regex.Match(command, @"printf %s '([^']*)'").Groups[1].Value;

        Assert.Equal(
            RemoteShellIntegrationSnippets.BuildInstallerScript(RemoteShellIntegrationShell.Fish),
            Decompress(payload));
    }

    /// <summary>
    /// The fish installer is POSIX sh carrying fish content: the wrapper must not have been written
    /// in fish by mistake, and the payload must be the fish snippet.
    /// </summary>
    [Fact]
    public void FishInstaller_IsPosixShCarryingTheFishSnippet()
    {
        string installer = RemoteShellIntegrationSnippets.BuildInstallerScript(
            RemoteShellIntegrationShell.Fish);

        Assert.StartsWith("#!/bin/sh", installer, StringComparison.Ordinal);
        Assert.Contains(
            RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish).TrimEnd('\n'),
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            RemoteShellIntegrationSnippets.GetRemotePath(RemoteShellIntegrationShell.Fish)
                .Replace("~/", string.Empty, StringComparison.Ordinal),
            installer,
            StringComparison.Ordinal);
    }

    internal static string Decompress(string base64)
    {
        byte[] raw = Convert.FromBase64String(base64);
        using var input = new MemoryStream(raw);
        using var gzip = new System.IO.Compression.GZipStream(
            input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
