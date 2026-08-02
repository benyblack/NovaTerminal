using System;
using System.Collections.Generic;
using System.IO;
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
                DisplayName: "bash / zsh",
                RemotePath: "~/.nova-shell-integration.sh",
                LoaderLine: "[ -f ~/.nova-shell-integration.sh ] && . ~/.nova-shell-integration.sh",
                LoaderTarget: "~/.bashrc (bash) or ~/.zshrc (zsh)"),
            [RemoteShellIntegrationShell.Fish] = new(
                FileName: "nova-shell-integration.fish",
                DisplayName: "fish",
                RemotePath: "~/.config/fish/conf.d/nova-shell-integration.fish",
                LoaderLine: null,
                LoaderTarget: null),
            [RemoteShellIntegrationShell.PowerShell] = new(
                FileName: "nova-shell-integration.ps1",
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
    /// The snippet text, exactly as shipped. This is what the Settings "Copy snippet" action puts on
    /// the clipboard: the whole file, install instructions included in its header comment, so a user
    /// who pastes it into <c>cat &gt; ...</c> and forgets the rest can read what to do next out of
    /// the file they just wrote.
    /// </summary>
    /// <remarks>
    /// Line endings are normalized to LF. The snippet is destined for a POSIX shell (or a pwsh that
    /// does not care), and a CRLF that survives a Windows checkout with
    /// <c>core.autocrlf=true</c> would give bash <c>$'\r': command not found</c> on every line.
    /// </remarks>
    public static string Read(RemoteShellIntegrationShell shell)
    {
        SnippetDescriptor descriptor = Get(shell);
        string resourceName = ResourcePrefix + descriptor.FileName;

        using Stream? stream = typeof(RemoteShellIntegrationSnippets).Assembly
            .GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidOperationException(
                $"Embedded shell-integration snippet '{resourceName}' is missing from " +
                $"{typeof(RemoteShellIntegrationSnippets).Assembly.GetName().Name}. It is embedded " +
                "from assets/shell-integration/ by NovaTerminal.CommandAssist.csproj.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
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
        string DisplayName,
        string RemotePath,
        string? LoaderLine,
        string? LoaderTarget);
}
