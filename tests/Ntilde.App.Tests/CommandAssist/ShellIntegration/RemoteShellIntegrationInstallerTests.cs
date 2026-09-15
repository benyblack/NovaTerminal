using System.Globalization;
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

        Match match = Regex.Match(command, PayloadPattern);

        Assert.True(match.Success, $"no single-quoted payload assignment in: {command}");
        Assert.Matches("^[A-Za-z0-9+/=]+$", match.Groups[1].Value);
    }

    [Fact]
    public void BashOrZshInstaller_PayloadDecodesToTheInstallerScript()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.BashOrZsh);
        string payload = ExtractPayload(command);

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

        Assert.Contains("set -l __nova_b '", command, StringComparison.Ordinal);
        Assert.Contains("set -l __nova_t (mktemp)", command, StringComparison.Ordinal);
        Assert.Contains("set -e __nova_b __nova_t __nova_d", command, StringComparison.Ordinal);
        Assert.DoesNotContain("$(mktemp)", command, StringComparison.Ordinal);
    }

    [Fact]
    public void FishInstaller_PayloadDecodesToTheInstallerScript()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.Fish);
        string payload = ExtractPayload(command);

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

    [Fact]
    public void PowerShellInstaller_IsExactlyOneLine()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.PowerShell);

        Assert.DoesNotContain("\n", command, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pure .NET: a remote pwsh on Windows has no base64 or gzip on PATH, and its `cat` is an alias
    /// for Get-Content - which is exactly why the old `cat &gt; file` recipe could not work there.
    /// </summary>
    [Fact]
    public void PowerShellInstaller_UsesNoExternalTools()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.PowerShell);

        Assert.Contains("[Convert]::FromBase64String(", command, StringComparison.Ordinal);
        Assert.Contains("GZipStream", command, StringComparison.Ordinal);
        Assert.DoesNotContain("base64 -d", command, StringComparison.Ordinal);
        Assert.DoesNotContain("gzip", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The call operator, not dot-sourcing: a child scope is what keeps the installer out of the
    /// user's session. A stray `. $__nova_t` here would reintroduce exactly what the design gave up.
    /// </summary>
    [Fact]
    public void PowerShellInstaller_InvokesTheScriptInAChildScope()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.PowerShell);

        Assert.Contains("& $__nova_t", command, StringComparison.Ordinal);
        Assert.DoesNotContain(". $__nova_t", command, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellInstaller_PayloadDecodesToTheInstallerScript()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.PowerShell);
        string payload = ExtractPayload(command);

        Assert.Matches("^[A-Za-z0-9+/=]+$", payload);
        Assert.Contains("FromBase64String($__nova_b)", command, StringComparison.Ordinal);
        Assert.Equal(
            RemoteShellIntegrationSnippets.BuildInstallerScript(
                RemoteShellIntegrationShell.PowerShell),
            Decompress(payload));
    }

    /// <summary>
    /// The here-string terminator is <c>'@</c> at the start of a line. A snippet line beginning with
    /// it would end the string early and turn the rest of the snippet into code.
    /// </summary>
    [Fact]
    public void PowerShellSnippet_DoesNotCollideWithTheHereStringTerminator()
    {
        string snippet = RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell);

        Assert.DoesNotContain(
            snippet.Split('\n'),
            line => line.StartsWith("'@", StringComparison.Ordinal));
    }

    // ---- truncation and length --------------------------------------------------------------

    /// <summary>
    /// The two arms that carry a payload-length check compare against a literal, and that literal
    /// is the length the payload actually has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the check catches is <em>mid-stream byte loss</em> - a flaky link, a multiplexer
    /// dropping a chunk of a paste - where bytes vanish from the middle and the tail still arrives.
    /// It does not catch a canonical-mode tty cut and cannot:
    /// <see cref="Integration.RemoteInstallerIntegrationTests.Installer_TruncatedAtTheCanonicalTtyLimit_FailsInTheShellBeforeAnyOfOurCodeRuns"/>
    /// runs that case and shows the shell rejecting the line on the unterminated quote, with none
    /// of the installer reached.
    /// </para>
    /// <para>
    /// Without the comparison, mid-stream loss surfaces only as a decode failure, and the
    /// installer's only honest guess would be "this host needs base64 and gzip" - on a host that
    /// has both. A literal that drifted out of step with the payload would be worse than none:
    /// every paste would report truncation. Hence both halves of this assertion.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh)]
    [InlineData(RemoteShellIntegrationShell.PowerShell)]
    public void Installer_ComparesThePayloadAgainstItsRealLength(RemoteShellIntegrationShell shell)
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(shell);

        // "-ne " cannot occur inside the payload: base64's alphabet is [A-Za-z0-9+/=].
        Match guard = Regex.Match(command, @"-ne (\d+)");

        Assert.True(guard.Success, $"no payload-length guard in: {command}");
        Assert.Equal(ExtractPayload(command).Length.ToString(CultureInfo.InvariantCulture), guard.Groups[1].Value);
    }

    /// <summary>
    /// Mid-stream loss and a broken decoder get different sentences. The point of the length check
    /// is that the installer stops having to blame one for the other.
    /// </summary>
    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh)]
    [InlineData(RemoteShellIntegrationShell.PowerShell)]
    public void Installer_DistinguishesTruncationFromAFailedDecode(RemoteShellIntegrationShell shell)
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(shell);

        Assert.Contains("cut short", command, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "install failed - this host needs base64 and gzip",
            command,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// fish carries no length check, and the reason is the number this test pins: fish is the one
    /// arm whose whole line fits under <c>N_TTY_BUF_SIZE</c>, so it is the one arm a tty cannot
    /// truncate - and a length check costs ~300 bytes of exactly the headroom that makes that true.
    /// Adding one would put fish on the edge of the failure it was meant to report.
    /// </summary>
    /// <remarks>
    /// The bound is 4096 rather than "current length plus slack" on purpose: 4096 is the property
    /// that matters. If a snippet edit ever pushes fish over it, the answer is to shrink the
    /// snippet or to accept the loss and add the check back - not to raise this number.
    /// </remarks>
    [Fact]
    public void FishInstaller_FitsUnderTheCanonicalTtyLineLimit_AndSoCarriesNoLengthCheck()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.Fish);

        Assert.True(
            command.Length < 4096,
            $"fish one-liner is {command.Length} chars, at or over the 4096-byte N_TTY_BUF_SIZE limit");
        Assert.DoesNotContain("cut short", command, StringComparison.Ordinal);
        Assert.DoesNotContain("string length", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tripwire, not a target: it exists so that a snippet edit which doubles the payload is
    /// noticed here rather than on a user's remote host. The docs page quotes "about 8.5 KB" for
    /// bash/zsh and pwsh; if this fails, re-measure and update it.
    /// </summary>
    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh, 10_240)]
    [InlineData(RemoteShellIntegrationShell.PowerShell, 10_240)]
    public void Installer_StaysWithinItsLengthBudget(RemoteShellIntegrationShell shell, int budget)
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(shell);

        Assert.True(
            command.Length <= budget,
            $"{shell} one-liner is {command.Length} chars, over the {budget} budget");
    }

    /// <summary>
    /// The <c>mktemp</c> fallback that used to be here - <c>printf /tmp/nova-si.%s "$$"</c> - is a
    /// local privilege-escalation hole and must not come back. <c>mktemp</c> gives 0600 and
    /// <c>O_EXCL</c>; a name built from <c>$$</c> (visible in <c>ps</c>) gives neither, and
    /// <c>&gt;</c> follows symlinks and leaves an existing file's mode and owner alone. Another
    /// local user pre-creates the path 0666 and rewrites it between the redirect and
    /// <c>sh "$__nova_t"</c>, or points it at <c>~/.bashrc</c> and lets the redirect truncate that.
    /// </summary>
    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh)]
    [InlineData(RemoteShellIntegrationShell.Fish)]
    public void ShInstallers_HaveNoPredictableTempFileFallback(RemoteShellIntegrationShell shell)
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(shell);

        Assert.Contains("mktemp", command, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/nova-si", command, StringComparison.Ordinal);
        Assert.DoesNotContain("$$", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>base64 -d</c> is the GNU spelling; macOS's <c>/usr/bin/base64</c> before Ventura only
    /// accepts <c>-D</c>. The sh arms probe once with a test vector rather than retrying the real
    /// payload, because the first attempt would already have consumed stdin. The variable holds the
    /// bare letter so fish's <c>set</c> never sees a value shaped like one of its own options.
    /// </summary>
    [Theory]
    [InlineData(RemoteShellIntegrationShell.BashOrZsh)]
    [InlineData(RemoteShellIntegrationShell.Fish)]
    public void ShInstallers_ProbeForTheBsdBase64DecodeFlag(RemoteShellIntegrationShell shell)
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(shell);

        Assert.Contains("printf %s Kg== | base64 -d >/dev/null 2>&1", command, StringComparison.Ordinal);
        Assert.Matches(@"__nova_d\s*=?\s*D\b", command);
        Assert.DoesNotContain("__nova_d -D", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The decode branch is taken on the pipeline's exit status, never on the temp file being
    /// non-empty: <c>gzip -dc</c> writes what it inflated before it failed, so a corrupt payload
    /// leaves a non-empty, syntactically broken installer that <c>[ -s ]</c> waves through and
    /// <c>sh</c> then runs - an unterminated heredoc writes a truncated snippet, "wrote" is
    /// printed, the loader line is appended, and every future shell sources a broken file.
    /// </summary>
    [Fact]
    public void BashOrZshInstaller_BranchesOnTheDecodeStatusNotTheFileSize()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.BashOrZsh);

        Assert.Contains(
            "if printf %s \"$__nova_b\" | base64 \"-$__nova_d\" | gzip -dc > \"$__nova_t\"; then",
            command,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[ -s \"$__nova_t\" ]", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fish arm had no error handling at all while its bash sibling had three checks. With no
    /// <c>mktemp</c>, <c>$__nova_t</c> expands to zero arguments, the redirect is a fish error, and
    /// <c>sh $__nova_t fish</c> degrades to <c>sh fish</c> - which fails silently as far as the user
    /// can tell.
    /// </summary>
    [Fact]
    public void FishInstaller_HandlesTheSameFailuresAsItsBashSibling()
    {
        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.Fish);

        Assert.Contains("if test -z \"$__nova_t\"", command, StringComparison.Ordinal);
        Assert.Contains("nova: install failed - mktemp", command, StringComparison.Ordinal);
        Assert.Contains(
            "if printf %s $__nova_b | base64 -$__nova_d | gzip -dc > $__nova_t;",
            command,
            StringComparison.Ordinal);
        Assert.Contains("nova: install failed - this host needs a working base64", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The single-quoted payload assignment each arm opens with. The blob is base64, so
    /// <c>[^']*</c> cannot terminate early.
    /// </summary>
    private const string PayloadPattern = @"__nova_b\s*=?\s*'([^']*)'";

    private static string ExtractPayload(string command)
    {
        Match match = Regex.Match(command, PayloadPattern);
        Assert.True(match.Success, $"no payload found in: {command}");
        return match.Groups[1].Value;
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
