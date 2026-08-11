using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace NovaTerminal.CommandAssist.ShellIntegration.Remote;

/// <summary>
/// Which remote shell a snippet is written for.
/// </summary>
/// <remarks>
/// Three rather than four, deliberately. bash and zsh share one file: their differences are
/// confined to the prompt-mark placement and the preexec mechanism, both of which the file
/// dispatches on at load time, and one file the user pastes without first having to know which
/// of the two they are on is worth more than the handful of lines it costs. fish gets its own
/// file because its syntax is not POSIX sh - `case`, `$-`, `local`, function and array syntax all
/// differ - so a dispatch would have to be a second file anyway.
/// </remarks>
public enum RemoteShellIntegrationShell
{
    /// <summary>bash and zsh, dispatched at load time inside the snippet.</summary>
    BashOrZsh,

    /// <summary>fish.</summary>
    Fish,

    /// <summary>PowerShell / pwsh.</summary>
    PowerShell,
}

/// <summary>
/// The shell-integration snippets a user installs on a remote host so that an SSH session gets the
/// same OSC 133 mark stream a local integrated shell does (V2 Phase 2b, Pillar 3).
/// </summary>
/// <remarks>
/// <para>
/// These are the remote counterpart of the <c>*BootstrapBuilder</c> classes and emit the same marks
/// with the same placement rules, but they are static files rather than generated strings. The
/// builders generate because they interpolate paths that only exist at launch time; a snippet the
/// user pastes has nothing to interpolate, and a file is reviewable, diffable, and can be shipped to
/// a host by any means the user likes.
/// </para>
/// <para>
/// They live at <c>assets/shell-integration/</c> in the repository and are embedded into this
/// assembly at build time. Embedded rather than copied next to the executable because the app is
/// published AOT and single-file: a resource stream is the one form guaranteed to survive that, and
/// the only consumer is a clipboard copy, which needs the text and not a path.
/// </para>
/// </remarks>
public static class RemoteShellIntegrationSnippets
{
    private const string ResourcePrefix = "NovaTerminal.CommandAssist.ShellIntegration.Remote.";

    /// <summary>
    /// Where the user is told to put each snippet. Kept next to the content so the docs page, the
    /// Settings affordance and the snippet's own header comment cannot drift apart.
    /// </summary>
    private static readonly IReadOnlyDictionary<RemoteShellIntegrationShell, SnippetDescriptor> Descriptors =
        new Dictionary<RemoteShellIntegrationShell, SnippetDescriptor>
        {
            [RemoteShellIntegrationShell.BashOrZsh] = new(
                FileName: "nova-shell-integration.sh",
                InstallerFileName: "nova-install.sh",
                DisplayName: "bash / zsh",
                RemotePath: "~/.nova-shell-integration.sh",
                LoaderLine: "[ -f ~/.nova-shell-integration.sh ] && . ~/.nova-shell-integration.sh",
                LoaderTarget: "~/.bashrc (bash) or ~/.zshrc (zsh)"),
            [RemoteShellIntegrationShell.Fish] = new(
                FileName: "nova-shell-integration.fish",
                InstallerFileName: "nova-install-fish.sh",
                DisplayName: "fish",
                RemotePath: "~/.config/fish/conf.d/nova-shell-integration.fish",
                LoaderLine: null,
                LoaderTarget: null),
            [RemoteShellIntegrationShell.PowerShell] = new(
                FileName: "nova-shell-integration.ps1",
                InstallerFileName: "nova-install.ps1",
                DisplayName: "PowerShell",
                RemotePath: "~/.nova-shell-integration.ps1",
                LoaderLine: ". ~/.nova-shell-integration.ps1",
                LoaderTarget: "$PROFILE"),
        };

    /// <summary>Every shell a snippet is shipped for, in the order the UI offers them.</summary>
    public static IReadOnlyList<RemoteShellIntegrationShell> All { get; } = new[]
    {
        RemoteShellIntegrationShell.BashOrZsh,
        RemoteShellIntegrationShell.Fish,
        RemoteShellIntegrationShell.PowerShell,
    };

    /// <summary>The label for <paramref name="shell"/> in a shell picker.</summary>
    public static string GetDisplayName(RemoteShellIntegrationShell shell) => Get(shell).DisplayName;

    /// <summary>The file name the snippet ships as.</summary>
    public static string GetFileName(RemoteShellIntegrationShell shell) => Get(shell).FileName;

    /// <summary>Where the user is told to write the snippet on the remote host.</summary>
    public static string GetRemotePath(RemoteShellIntegrationShell shell) => Get(shell).RemotePath;

    /// <summary>
    /// The line the user adds to their rc file, or <see langword="null"/> when the snippet's install
    /// location is auto-sourced (fish's <c>conf.d</c>) and there is nothing to add.
    /// </summary>
    public static string? GetLoaderLine(RemoteShellIntegrationShell shell) => Get(shell).LoaderLine;

    /// <summary>The rc file <see cref="GetLoaderLine"/> belongs in, or <see langword="null"/>.</summary>
    public static string? GetLoaderTarget(RemoteShellIntegrationShell shell) => Get(shell).LoaderTarget;

    /// <summary>
    /// The snippet text, exactly as shipped, backing the "Copy plain snippet" action. This is the
    /// whole file, install instructions included in its header comment, so a user who pastes it into
    /// <c>cat &gt; ...</c> and forgets the rest can read what to do next out of the file they just
    /// wrote.
    /// </summary>
    /// <remarks>
    /// Line endings are normalized to LF. The snippet is destined for a POSIX shell (or a pwsh that
    /// does not care), and a CRLF that survives a Windows checkout with
    /// <c>core.autocrlf=true</c> would give bash <c>$'\r': command not found</c> on every line.
    /// </remarks>
    public static string Read(RemoteShellIntegrationShell shell) => ReadResource(Get(shell).FileName);

    /// <summary>
    /// The one-line command Settings' "Copy installer" action puts on the clipboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One line, because one line is one history entry and one prompt cycle. The alternative - the
    /// user pastes the 300-line snippet itself - floods the scrollback, is slow to redraw in any
    /// shell with syntax highlighting, leaves the rc edit as a manual step users forget, and on a
    /// Windows remote cannot work at all (<c>cat</c> there is an alias for <c>Get-Content</c>, so
    /// <c>cat &gt; file</c> has no input). That path is still one click away as "Copy plain
    /// snippet"; it is just not the default.
    /// </para>
    /// <para>
    /// The payload is gzip+base64, roughly half the bytes of the installer script it decodes to,
    /// and base64's alphabet contains no shell metacharacter, so the single quotes around it can
    /// never need escaping. Exact sizes are not quoted here because they move with every snippet
    /// edit; <c>Installer_StaysWithinItsLengthBudget</c> in RemoteShellIntegrationInstallerTests is
    /// where the current measurement lives. It decodes to an installer script that runs as a
    /// <em>child process</em> - the live shell is never sourced into, and the shell's identity
    /// reaches the installer as an argument the live shell expanded.
    /// </para>
    /// <para>
    /// A base64 blob cannot be read before it runs, which is a real objection to shipping one. It
    /// is answered by what the blob contains: a gzipped copy of a reviewable file under
    /// <c>assets/shell-integration/install/</c>, and nothing else - plus <see cref="Read"/> still
    /// backing a "Copy plain snippet" action in the same row for a user who would rather place the
    /// file themselves. See docs/command-assist/RemoteShellIntegration.md for what that does and
    /// does not buy.
    /// </para>
    /// <para>
    /// <b>The temp file comes from <c>mktemp</c> or the install does not happen.</b> There is no
    /// fallback path. The obvious one - <c>printf /tmp/nova-si.%s "$$"</c> - was tried and removed:
    /// <c>mktemp</c> gives 0600 and <c>O_EXCL</c>, that name gives neither. <c>$$</c> is visible in
    /// <c>ps</c>, so the path is predictable, and <c>&gt;</c> follows symlinks and leaves an
    /// existing file's mode and owner alone. On a shared host another local user can pre-create it
    /// 0666 and rewrite the contents between the redirect and <c>sh "$__nova_t"</c> - code
    /// execution as the victim - or point it at <c>~/.bashrc</c> and have the redirect truncate
    /// that instead, no race required. A safe fallback would need <c>set -C</c> plus an
    /// unpredictable name, which is more machinery than a rarely-taken path is worth; failing with
    /// a message naming <c>mktemp</c> is the whole of the recovery story.
    /// </para>
    /// <para>
    /// <b>The bash/zsh and pwsh arms carry their own payload length and check it before decoding.</b>
    /// What that catches is <em>mid-stream byte loss</em>: a flaky link, a multiplexer dropping a
    /// chunk of a paste, anything that removes bytes from the middle while the tail still arrives.
    /// Without it the shortened payload reaches the decoder and the only symptom is a decode
    /// failure, which the installer would have to blame on missing <c>base64</c>/<c>gzip</c> -
    /// sending the user to install coreutils on a host that already has them.
    /// </para>
    /// <para>
    /// It deliberately does <em>not</em> catch a canonical-mode tty cut, and cannot. The line is
    /// over 4 KB and <c>N_TTY_BUF_SIZE</c> is 4096, so a tty in canonical mode
    /// (<c>docker exec -it c sh</c>, busybox <c>ash</c>, <c>dash</c> as <c>/bin/sh</c>, serial and
    /// IPMI consoles, pwsh without PSReadLine) discards everything past 4096 bytes of the line. But
    /// the payload literal opens at byte 9 (bash) or 10 (pwsh) and closes past 7500, so a tail cut
    /// at 4096 always lands <em>inside</em> the quoted blob and takes the closing quote with it:
    /// bash answers <c>unexpected EOF while looking for matching '</c> and pwsh
    /// <c>The string is missing the terminator: '.</c>, and none of our code runs at all. There is
    /// no arrangement of a tail truncation that leaves the guard reachable. Interactive bash, zsh
    /// and fish read in raw mode via readline/ZLE and never hit this, which is why pasting by hand
    /// always works.
    /// </para>
    /// <para>
    /// The <b>fish</b> arm has no length check. Its payload is a third the size and the whole line
    /// fits under 4096, so it is the one arm that cannot be truncated by a tty in the first place -
    /// and the check is 300-odd bytes of the headroom that makes that true. Its other guards
    /// (<c>mktemp</c>, the decode status, both failure messages) are the same as bash's.
    /// </para>
    /// <para>
    /// The decode branch is chosen on the <em>pipeline's exit status</em>, not on the temp file
    /// being non-empty. <c>gzip -dc</c> writes what it managed to inflate before it fails, so a
    /// corrupt payload leaves a non-empty, syntactically broken installer that <c>[ -s ]</c> would
    /// wave through and <c>sh</c> would then run: an unterminated heredoc writes a truncated
    /// snippet, the installer reports "wrote", appends the loader line, and every future shell on
    /// that host sources a broken file.
    /// </para>
    /// <para>
    /// The pwsh arm's <c>&amp; $__nova_t</c> sits <em>outside</em> the decode's <c>try</c>, gated on a
    /// success flag. Inside it, a terminating error raised by the installer script itself would be
    /// caught by the decode's handler and reported as "the payload did not unpack" - a diagnosis
    /// about the blob for a failure that had nothing to do with it.
    /// </para>
    /// <para>
    /// <c>base64 -d</c> is the GNU spelling. macOS's <c>/usr/bin/base64</c> predating Ventura only
    /// accepts <c>-D</c>, so the sh and fish arms probe with a four-character test vector once and
    /// fall back. A <c>-d ||</c> <c>-D</c> retry on the real payload is not an option: the first
    /// attempt would already have consumed stdin. The variable holds the bare letter and the dash
    /// is concatenated at the call site, so that fish's <c>set</c> never sees a value that looks
    /// like one of its own options.
    /// </para>
    /// </remarks>
    public static string BuildInstallerCommand(RemoteShellIntegrationShell shell)
    {
        string blob = Compress(BuildInstallerScript(shell));

        string template = shell switch
        {
            RemoteShellIntegrationShell.BashOrZsh =>
                """
                __nova_b='@@BLOB@@'; if [ ${#__nova_b} -ne @@BLOBLEN@@ ]; then echo "nova: install failed - the pasted line was cut short (${#__nova_b} of @@BLOBLEN@@ payload characters). A terminal in canonical mode drops everything past 4096 bytes of one line; use Copy plain snippet on this host."; elif __nova_t=$(mktemp); then __nova_d=d; printf %s Kg== | base64 -d >/dev/null 2>&1 || __nova_d=D; if printf %s "$__nova_b" | base64 "-$__nova_d" | gzip -dc > "$__nova_t"; then sh "$__nova_t" "${ZSH_VERSION:+zsh}${BASH_VERSION:+bash}"; else echo "nova: install failed - this host needs a working base64 and gzip to unpack the payload"; fi; rm -f "$__nova_t"; else echo "nova: install failed - mktemp could not create a temp file"; fi; unset __nova_b __nova_t __nova_d
                """,
            RemoteShellIntegrationShell.Fish =>
                """
                set -l __nova_b '@@BLOB@@'; set -l __nova_t (mktemp); set -l __nova_d d; if test -z "$__nova_t"; echo "nova: install failed - mktemp could not create a temp file"; else; printf %s Kg== | base64 -d >/dev/null 2>&1; or set __nova_d D; if printf %s $__nova_b | base64 -$__nova_d | gzip -dc > $__nova_t; sh $__nova_t fish; else; echo "nova: install failed - this host needs a working base64 and gzip to unpack the payload"; end; rm -f $__nova_t; end; set -e __nova_b __nova_t __nova_d
                """,
            RemoteShellIntegrationShell.PowerShell =>
                """
                $__nova_b='@@BLOB@@'; if($__nova_b.Length -ne @@BLOBLEN@@){Write-Host "nova: install failed - the pasted line was cut short ($($__nova_b.Length) of @@BLOBLEN@@ payload characters). A console in canonical mode drops everything past 4096 bytes of one line; use Copy plain snippet on this host."}else{$__nova_t=[IO.Path]::GetTempPath()+[Guid]::NewGuid().ToString('N')+'.ps1'; try{$__nova_g=[IO.Compression.GZipStream]::new([IO.MemoryStream]::new([Convert]::FromBase64String($__nova_b)),[IO.Compression.CompressionMode]::Decompress); $__nova_o=[IO.File]::Create($__nova_t); $__nova_g.CopyTo($__nova_o); $__nova_o.Dispose(); $__nova_g.Dispose(); $__nova_ok=$true}catch{Write-Host "nova: install failed - the payload did not unpack: $($_.Exception.Message)"}finally{if($__nova_o){$__nova_o.Dispose()}; if($__nova_g){$__nova_g.Dispose()}}; if($__nova_ok){& $__nova_t}; Remove-Item $__nova_t -ErrorAction SilentlyContinue}; Remove-Variable __nova_b,__nova_t,__nova_g,__nova_o,__nova_ok -ErrorAction SilentlyContinue
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "No installer ships for this shell."),
        };

        return template
            .Replace("@@BLOBLEN@@", blob.Length.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@@BLOB@@", blob, StringComparison.Ordinal);
    }

    /// <summary>
    /// The installer script the one-liner's payload decodes to: the template for
    /// <paramref name="shell"/> with its snippet substituted in.
    /// </summary>
    /// <remarks>
    /// Internal because the round-trip test needs the expectation. The delimiter guard is not
    /// defensive noise: a snippet line that collided with the heredoc terminator would silently
    /// truncate the installed file rather than fail, and the failure would surface on the user's
    /// remote host rather than here.
    /// </remarks>
    internal static string BuildInstallerScript(RemoteShellIntegrationShell shell) =>
        BuildInstallerScript(shell, ReadResource(Get(shell).FileName));

    /// <summary>
    /// <see cref="BuildInstallerScript(RemoteShellIntegrationShell)"/> with the snippet supplied.
    /// </summary>
    /// <remarks>
    /// The overload exists for the delimiter-guard test: no shipped snippet collides, and one that
    /// did would be a bug found on a user's remote host rather than here.
    /// </remarks>
    internal static string BuildInstallerScript(RemoteShellIntegrationShell shell, string snippet)
    {
        SnippetDescriptor descriptor = Get(shell);
        string template = ReadResource(descriptor.InstallerFileName);

        // The delimiter that would end the embedded literal early: a heredoc terminator for the sh
        // installers, the here-string terminator for the PowerShell one. A snippet line starting
        // with it would truncate the installed file - or, in PowerShell, turn the remainder of the
        // snippet into code.
        string delimiter = shell == RemoteShellIntegrationShell.PowerShell
            ? "'@"
            : "__NOVA_SNIPPET_EOF__";

        foreach (string line in snippet.Split('\n'))
        {
            if (line.StartsWith(delimiter, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Snippet '{descriptor.FileName}' contains a line starting with '{delimiter}', " +
                    "the installer template's terminator, which would truncate the installed file. " +
                    "Rename the terminator in the installer template.");
            }
        }

        return template.Replace("@@NOVA_SNIPPET@@", snippet.TrimEnd('\n'), StringComparison.Ordinal);
    }

    private static string ReadResource(string fileName)
    {
        string resourceName = ResourcePrefix + fileName;

        using Stream? stream = typeof(RemoteShellIntegrationSnippets).Assembly
            .GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidOperationException(
                $"Embedded shell-integration resource '{resourceName}' is missing from " +
                $"{typeof(RemoteShellIntegrationSnippets).Assembly.GetName().Name}. It is embedded " +
                "from assets/shell-integration/ by NovaTerminal.CommandAssist.csproj.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static string Compress(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>
    /// What the user does next, in one short paragraph, for display beside the copy action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the secondary path. The primary one is <see cref="BuildInstallerCommand"/>, and these
    /// instructions belong to the "Copy plain snippet" action beside it. It exists for the user who
    /// wants to read the file before trusting it, or who is placing it from a dotfiles repo or
    /// <c>/etc/profile.d</c> rather than pasting at a prompt - the one thing a base64 one-liner
    /// cannot offer.
    /// </para>
    /// <para>
    /// It is not the default because of what it costs when it is: a 300-line paste floods the
    /// scrollback, is slow to redraw in any shell with syntax highlighting, leaves the rc edit to
    /// the user, and on a Windows remote cannot work at all, where <c>cat</c> is an alias for
    /// <c>Get-Content</c> and so <c>cat &gt; file</c> has no input to read.
    /// </para>
    /// <para>
    /// <c>cat &gt; path</c> plus Ctrl-D rather than an editor: it is the one recipe that works on a
    /// host with no editor configured, no <c>$EDITOR</c>, and a bracketed-paste-aware shell.
    /// </para>
    /// </remarks>
    public static string BuildInstallInstructions(RemoteShellIntegrationShell shell)
    {
        SnippetDescriptor descriptor = Get(shell);
        var builder = new StringBuilder();
        builder.Append("Copied ").Append(descriptor.FileName).Append(". On the remote host run  ");

        if (shell == RemoteShellIntegrationShell.Fish)
        {
            builder.Append("mkdir -p ~/.config/fish/conf.d && cat > ").Append(descriptor.RemotePath);
        }
        else
        {
            builder.Append("cat > ").Append(descriptor.RemotePath);
        }

        builder.Append("  then paste and press Ctrl-D.");

        if (descriptor.LoaderLine == null)
        {
            builder.Append(" fish sources conf.d automatically, so there is nothing else to add.");
        }
        else
        {
            builder.Append(" Then add  ").Append(descriptor.LoaderLine)
                .Append("  to ").Append(descriptor.LoaderTarget)
                .Append(" and open a new session to that host.");
        }

        return builder.ToString();
    }

    private static SnippetDescriptor Get(RemoteShellIntegrationShell shell)
    {
        return Descriptors.TryGetValue(shell, out SnippetDescriptor? descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(shell), shell, "No snippet ships for this shell.");
    }

    private sealed record SnippetDescriptor(
        string FileName,
        string InstallerFileName,
        string DisplayName,
        string RemotePath,
        string? LoaderLine,
        string? LoaderTarget);
}
