using System;
using System.Collections.Generic;
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
    /// One line, because one line is one history entry and one prompt cycle. The 300-line paste this
    /// replaced flooded the scrollback, was slow to redraw in any shell with syntax highlighting,
    /// left the rc edit as a manual step users forget, and on a Windows remote could not work at all
    /// (<c>cat</c> there is an alias for <c>Get-Content</c>, so <c>cat &gt; file</c> has no input).
    /// </para>
    /// <para>
    /// The payload is gzip+base64: 6.4 KB instead of 17.0 KB for the bash/zsh snippet, and base64's
    /// alphabet contains no shell metacharacter, so the single quotes around it can never need
    /// escaping. It decodes to an installer script that runs as a <em>child process</em> - the live
    /// shell is never sourced into, and the shell's identity reaches the installer as an argument
    /// the live shell expanded.
    /// </para>
    /// <para>
    /// This reverses the argument the class used to make against generated installers, which was
    /// that a blob cannot be read before it runs. It is answered instead by the installers being
    /// reviewable files under <c>assets/shell-integration/install/</c> and by
    /// <see cref="Read"/> still backing a "Copy plain snippet" action in the same row.
    /// </para>
    /// </remarks>
    public static string BuildInstallerCommand(RemoteShellIntegrationShell shell)
    {
        string blob = Compress(BuildInstallerScript(shell));

        string template = shell switch
        {
            RemoteShellIntegrationShell.BashOrZsh =>
                """
                __nova_t=$(mktemp 2>/dev/null || printf /tmp/nova-si.%s "$$"); printf %s '@@BLOB@@' | base64 -d 2>/dev/null | gzip -dc 2>/dev/null > "$__nova_t"; if [ -s "$__nova_t" ]; then sh "$__nova_t" "${ZSH_VERSION:+zsh}${BASH_VERSION:+bash}"; else echo "nova: install failed - this host needs base64 and gzip"; fi; rm -f "$__nova_t"; unset __nova_t
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "No installer ships for this shell."),
        };

        return template.Replace("@@BLOB@@", blob, StringComparison.Ordinal);
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

        const string Delimiter = "__NOVA_SNIPPET_EOF__";
        foreach (string line in snippet.Split('\n'))
        {
            if (line.StartsWith(Delimiter, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Snippet '{descriptor.FileName}' contains a line starting with the installer's " +
                    $"heredoc delimiter '{Delimiter}', which would truncate the installed file. " +
                    "Rename the delimiter in the installer template.");
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
    /// The clipboard carries the snippet itself rather than a generated one-line installer, and the
    /// instructions are shown rather than pasted. A generated installer would have to be a heredoc
    /// (which fish cannot parse), a base64 blob (which the user cannot read before running), or a
    /// download (which needs a URL the app does not have). Handing over the file and telling the
    /// user where to put it is the version with no hidden machinery, and the snippet repeats these
    /// same steps in its own header comment so they survive the trip to the remote host.
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
