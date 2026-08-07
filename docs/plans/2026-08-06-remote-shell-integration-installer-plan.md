# Remote Shell Integration One-Line Installer — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Settings' "copy 300 lines and paste them into `cat >`" remote shell-integration flow with a single line the user pastes at the remote prompt, which writes a temp file, runs it as a child process, and deletes it.

**Architecture:** `RemoteShellIntegrationSnippets` gains `BuildInstallerCommand(shell)`. It reads a new installer template asset, substitutes the existing snippet into the template's `@@NOVA_SNIPPET@@` line, gzips, base64-encodes, and wraps the blob in a per-shell one-liner. The installer runs as a **child process** (`sh "$t" "$shell"` / `& $t`) and is never sourced into the live shell — the shell's identity is expanded by the live shell and passed in as an argument instead. The Settings row keeps the shell picker, promotes **Copy installer** to primary, and keeps today's whole-file copy as **Copy plain snippet**.

**Tech Stack:** C# / .NET 10, Avalonia (Settings window), xunit, POSIX sh + PowerShell installer assets, `System.IO.Compression.GZipStream`.

**Design doc:** [docs/plans/2026-08-06-remote-shell-integration-installer-design.md](2026-08-06-remote-shell-integration-installer-design.md) — read it before Task 1.

## Global Constraints

- **Build and test only via the wrappers:** `scripts/build.ps1 <args>` (PowerShell) or `scripts/build.sh <args>`. A raw `dotnet build` hangs when stdout is captured.
- **Run tests targeted, never solution-wide.** Full-solution `dotnet test` is 20–30 minutes here. Every test step in this plan names one project plus a `--filter`.
- **All new assets are LF-only.** A CRLF surviving a Windows checkout gives bash `$'\r': command not found` on every line. `RemoteShellIntegrationSnippets.Read` already normalizes, and the new reader path must too.
- **Asset files live in `assets/shell-integration/install/`** and are embedded via `EmbeddedResource` + explicit `LogicalName` in `src/NovaTerminal.CommandAssist/NovaTerminal.CommandAssist.csproj`, exactly like the three existing snippets. The files live outside the project directory, so a missing `LogicalName` produces a missing resource at runtime rather than a build error.
- **Embedded resource logical-name prefix:** `NovaTerminal.CommandAssist.ShellIntegration.Remote.` (const `ResourcePrefix`, already defined).
- **Paths and loader lines are never re-typed.** Anything user-visible comes from the existing `SnippetDescriptor` (`GetRemotePath`, `GetLoaderLine`, `GetLoaderTarget`, `GetFileName`, `GetDisplayName`).
- **No new `TerminalSettings` field.** This row is an action, not a preference — so `TerminalPane.ApplySettings`'s effective-settings whitelist is not involved.
- **The installer must never be sourced into the live shell**, and must not leave any variable or function behind in it beyond the one-liner's own `__nova_t`, which is unset on the same line.
- **Every generated command is exactly one line** — no `\n`, no `\r`, anywhere in the returned string.
- **Test namespaces:** static tests `NovaTerminal.Tests.CommandAssist.ShellIntegration`; shell-running tests `NovaTerminal.Tests.CommandAssist.ShellIntegration.Integration`, marked `[Trait("Category", "ShellIntegration")]` and `[Collection(nameof(ShellIntegrationCollection))]`.
- **`nova:` output strings are contracts.** Tests assert on them; copy them verbatim from this plan.

---

## File Structure

| File | Responsibility |
|---|---|
| `assets/shell-integration/install/nova-install.sh` | **Create.** POSIX sh installer for bash and zsh: writes the snippet, resolves the rc file from `$1`, patches it idempotently, prints what it did. |
| `assets/shell-integration/install/nova-install-fish.sh` | **Create.** POSIX sh installer for the fish snippet: creates `conf.d`, writes the file. No rc step. |
| `assets/shell-integration/install/nova-install.ps1` | **Create.** PowerShell installer: writes the snippet, ensures `$PROFILE`'s directory, patches `$PROFILE` idempotently. Takes `-ProfilePath`/`-DestDir` so it is testable. |
| `src/NovaTerminal.CommandAssist/NovaTerminal.CommandAssist.csproj` | **Modify.** Three more `EmbeddedResource` entries. |
| `src/NovaTerminal.CommandAssist/ShellIntegration/Remote/RemoteShellIntegrationSnippets.cs` | **Modify.** `InstallerFileName` on the descriptor; `ReadResource` extracted; `BuildInstallerScript` (internal, for tests); `BuildInstallerCommand` (public); `Compress`. |
| `tests/NovaTerminal.App.Tests/CommandAssist/ShellIntegration/RemoteShellIntegrationInstallerTests.cs` | **Create.** Static assertions: one line, base64 charset, round-trip, collision guard, descriptor agreement. |
| `tests/NovaTerminal.App.Tests/CommandAssist/ShellIntegration/Integration/RemoteInstallerIntegrationTests.cs` | **Create.** Runs the generated command through a real bash with `HOME` redirected: fresh install, re-run, hand-placed loader, decode failure, marks flow in a new shell. |
| `src/NovaTerminal.App/SettingsWindow.axaml` | **Modify (line ~636).** Rename the primary button, add the secondary one, update the row description. |
| `src/NovaTerminal.App/SettingsWindow.axaml.cs` | **Modify (lines 1447–1498).** Wire both buttons; new status text. |
| `docs/command-assist/RemoteShellIntegration.md` | **Modify.** § Install rewritten; the broken PowerShell `cat >` recipe removed. |

---

## Task 1: The sh installer asset and `BuildInstallerCommand` for bash/zsh

**Files:**
- Create: `assets/shell-integration/install/nova-install.sh`
- Modify: `src/NovaTerminal.CommandAssist/NovaTerminal.CommandAssist.csproj:17-24`
- Modify: `src/NovaTerminal.CommandAssist/ShellIntegration/Remote/RemoteShellIntegrationSnippets.cs`
- Test: `tests/NovaTerminal.App.Tests/CommandAssist/ShellIntegration/RemoteShellIntegrationInstallerTests.cs`

**Interfaces:**
- Consumes: existing `RemoteShellIntegrationSnippets.Read(shell)`, `Get(shell)`, `ResourcePrefix`, `SnippetDescriptor`.
- Produces:
  - `public static string BuildInstallerCommand(RemoteShellIntegrationShell shell)` — the one-line command.
  - `internal static string BuildInstallerScript(RemoteShellIntegrationShell shell)` — the decoded installer text, before compression. Tests use it as the round-trip expectation.
  - `internal static string BuildInstallerScript(RemoteShellIntegrationShell shell, string snippet)` — same, with the snippet supplied. The one-argument form delegates to it. This exists so the delimiter guard can be tested by feeding it a colliding snippet, which is impossible through the shipped assets.
  - `private static string ReadResource(string fileName)` — LF-normalized resource text.
  - `private static string Compress(string text)` — gzip + base64, no line breaks.
  - `SnippetDescriptor` gains `string InstallerFileName`.
  - Throws `InvalidOperationException` when the snippet collides with the template delimiter.

- [ ] **Step 1: Write the installer asset**

Create `assets/shell-integration/install/nova-install.sh` with LF endings:

```sh
#!/bin/sh
# Nova Terminal remote shell integration installer (bash and zsh).
#
# Settings copies a one-line command that decodes this file into a temp file, runs it as a CHILD
# process, and deletes it. It is deliberately never sourced into your interactive shell: $1 carries
# the shell name, expanded by the live shell inside that one-liner, so nothing has to be sourced to
# find out which rc file to patch, and nothing this file defines can leak into your session.
#
# It writes ~/.nova-shell-integration.sh, adds the loader line to the matching rc file if it is not
# already there, and prints what it did. Running it twice changes nothing the second time.

__nova_shell="$1"
if [ -z "$__nova_shell" ]; then
    __nova_shell=$(basename "${SHELL:-}" 2>/dev/null)
fi

__nova_dest="$HOME/.nova-shell-integration.sh"

cat > "$__nova_dest" <<'__NOVA_SNIPPET_EOF__'
@@NOVA_SNIPPET@@
__NOVA_SNIPPET_EOF__

if [ ! -s "$__nova_dest" ]; then
    echo "nova: could not write $__nova_dest"
    exit 1
fi
echo "nova: wrote ~/.nova-shell-integration.sh"

case "$__nova_shell" in
    zsh)
        __nova_rc="$HOME/.zshrc"
        __nova_rc_display="~/.zshrc"
        ;;
    bash)
        __nova_rc="$HOME/.bashrc"
        __nova_rc_display="~/.bashrc"
        ;;
    *)
        __nova_rc=""
        __nova_rc_display=""
        ;;
esac

__nova_loader='[ -f ~/.nova-shell-integration.sh ] && . ~/.nova-shell-integration.sh'

if [ -z "$__nova_rc" ]; then
    echo "nova: could not tell which shell you use - add this line to your rc file:"
    echo "nova:   $__nova_loader"
elif [ -f "$__nova_rc" ] && grep -q 'nova-shell-integration' "$__nova_rc" 2>/dev/null; then
    echo "nova: loader line already in $__nova_rc_display - unchanged"
else
    printf '%s\n' "$__nova_loader" >> "$__nova_rc"
    echo "nova: added loader line to $__nova_rc_display"
fi

echo "nova: run  . ~/.nova-shell-integration.sh  to enable it in this session,"
echo "nova: or open a new Nova session to this host."
```

Two things to preserve if you edit it: the loader line must be byte-identical to `GetLoaderLine(BashOrZsh)` (Task 1 Step 6 asserts this), and the `grep -q 'nova-shell-integration'` marker must match a hand-placed loader line as well as ours.

- [ ] **Step 2: Embed it**

In `src/NovaTerminal.CommandAssist/NovaTerminal.CommandAssist.csproj`, inside the existing `ItemGroup` that holds the three snippets (line 17), add:

```xml
    <EmbeddedResource Include="$(MSBuildThisFileDirectory)..\..\assets\shell-integration\install\nova-install.sh"
                      LogicalName="NovaTerminal.CommandAssist.ShellIntegration.Remote.nova-install.sh" />
```

- [ ] **Step 3: Write the failing tests**

Create `tests/NovaTerminal.App.Tests/CommandAssist/ShellIntegration/RemoteShellIntegrationInstallerTests.cs`:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~RemoteShellIntegrationInstallerTests"
```

Expected: compile error — `BuildInstallerCommand` and `BuildInstallerScript` do not exist.

- [ ] **Step 5: Add `InstallerFileName` to the descriptor**

In `RemoteShellIntegrationSnippets.cs`, extend the record at the bottom of the file:

```csharp
    private sealed record SnippetDescriptor(
        string FileName,
        string InstallerFileName,
        string DisplayName,
        string RemotePath,
        string? LoaderLine,
        string? LoaderTarget);
```

and give each entry in `Descriptors` its installer, keeping the existing values:

```csharp
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
```

- [ ] **Step 6: Extract `ReadResource` and implement the composition**

Replace the body of `Read` so both it and the installer path share one reader, and add the new members. Add `using System.IO.Compression;` at the top of the file.

```csharp
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
```

Keep `Read`'s existing XML doc comment — it now documents the "Copy plain snippet" path, so amend its first sentence to say so.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~RemoteShellIntegrationInstallerTests"
```

Expected: 5 passed. If `PayloadDecodesToTheInstallerScript` fails on trailing whitespace, the cause is `TrimEnd('\n')` being applied in one place and not the other — fix the production side, not the test.

- [ ] **Step 8: Add the collision-guard tests**

Append to `RemoteShellIntegrationInstallerTests`:

```csharp
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
    public void NoSnippet_CollidesWithTheInstallerDelimiter(RemoteShellIntegrationShell shell)
    {
        string installer = RemoteShellIntegrationSnippets.BuildInstallerScript(shell);
        string snippet = RemoteShellIntegrationSnippets.Read(shell);

        Assert.Contains("__NOVA_SNIPPET_EOF__", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("__NOVA_SNIPPET_EOF__", snippet, StringComparison.Ordinal);
    }
```

- [ ] **Step 9: Run the tests to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~RemoteShellIntegrationInstallerTests"
```

Expected: 7 passed.

- [ ] **Step 10: Commit**

```bash
git add assets/shell-integration/install/nova-install.sh src/NovaTerminal.CommandAssist tests/NovaTerminal.App.Tests/CommandAssist/ShellIntegration/RemoteShellIntegrationInstallerTests.cs
git commit -m "feat(command-assist): one-line installer command for the bash/zsh remote snippet"
```

---

## Task 2: Prove the sh installer works, on a real bash

**Files:**
- Test: `tests/NovaTerminal.App.Tests/CommandAssist/ShellIntegration/Integration/RemoteInstallerIntegrationTests.cs` (create)

**Interfaces:**
- Consumes: `RemoteShellIntegrationSnippets.BuildInstallerCommand` (Task 1); `ShellHarness.FindBash()`, `ShellHarness.Run(shellPath, arguments, scriptedStdin, environmentOverrides, timeout)`, `HarnessResult`, `OscEvent` from `tests/NovaTerminal.App.Tests/CommandAssist/ShellIntegration/Integration/ShellHarness.cs`.
- Produces: nothing consumed by later tasks.

Why a separate task: Task 1's assertions are all about text. This one runs the command a user would paste and looks at the filesystem afterwards, which is the only way to catch a quoting slip, a heredoc that eats a line, or non-idempotent rc patching. It is also the layer that would catch `grep -q` matching too loosely.

Note the harness split. Running the *installer* needs no TTY, so it goes through `Process.Start` with `bash -c` — cheap and deterministic. Proving *marks flow afterwards* needs an interactive shell on a real PTY, which is what `ShellHarness.Run` exists for.

- [ ] **Step 1: Write the failing tests**

Create the file:

```csharp
using System.Diagnostics;
using System.Text;
using NovaTerminal.CommandAssist.ShellIntegration.Remote;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration.Integration;

/// <summary>
/// The generated one-liner, run the way a user pastes it: through a real bash, with
/// <c>HOME</c> redirected to a temp directory.
/// </summary>
/// <remarks>
/// <para>
/// RemoteShellIntegrationInstallerTests asserts on the command's text, which cannot see the bugs
/// this file was written for: a heredoc that drops a line, an rc file patched twice, a
/// <c>grep -q</c> marker that misses a hand-placed loader line, or a decode failure that writes an
/// empty snippet and reports success.
/// </para>
/// <para>
/// The installer itself needs no TTY, so it runs under <c>bash -c</c> via
/// <see cref="Process"/>. Only the last test - "and afterwards the marks actually flow" - needs an
/// interactive shell, and that one goes through <see cref="ShellHarness"/> on a real PTY.
/// </para>
/// <para>
/// Skipped when bash is absent. <c>HOME</c> is a per-test temp directory so the developer's own
/// dotfiles are never touched, and it is passed with forward slashes because Git Bash resolves
/// <c>$HOME/...</c> more predictably that way.
/// </para>
/// </remarks>
[Trait("Category", "ShellIntegration")]
[Collection(nameof(ShellIntegrationCollection))]
public sealed class RemoteInstallerIntegrationTests : IDisposable
{
    private readonly string _home;

    public RemoteInstallerIntegrationTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"nova_installer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private string HomeForShell => _home.Replace('\\', '/');

    private string SnippetPath => Path.Combine(_home, ".nova-shell-integration.sh");

    private string BashrcPath => Path.Combine(_home, ".bashrc");

    /// <summary>Runs the pasted one-liner under a non-interactive bash and returns its output.</summary>
    private string RunInstaller(string? pathOverride = null)
    {
        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.BashOrZsh);

        var startInfo = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        startInfo.Environment["HOME"] = HomeForShell;
        if (pathOverride is not null)
        {
            startInfo.Environment["PATH"] = pathOverride;
        }

        using Process process = Process.Start(startInfo)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "installer did not finish within 30s");

        return stdout + stderr;
    }

    private static int CountLoaderLines(string rcContent) => rcContent
        .Split('\n')
        .Count(line => line.Contains("nova-shell-integration", StringComparison.Ordinal));

    // ---- the happy path -------------------------------------------------------------------------

    [Fact]
    public void Installer_WritesTheSnippetByteForByte()
    {
        string output = RunInstaller();

        Assert.True(File.Exists(SnippetPath), $"snippet not written. output:\n{output}");
        Assert.Equal(
            RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh).TrimEnd('\n'),
            File.ReadAllText(SnippetPath).Replace("\r\n", "\n").TrimEnd('\n'));
        Assert.Contains("nova: wrote ~/.nova-shell-integration.sh", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rc edit is the step users forget, so the installer does it - and it detects bash from the
    /// <c>${BASH_VERSION:+bash}</c> the live shell expands into the child's argv, with nothing
    /// sourced.
    /// </summary>
    [Fact]
    public void Installer_AddsTheLoaderLineToBashrc()
    {
        string output = RunInstaller();

        Assert.True(File.Exists(BashrcPath), $"~/.bashrc not created. output:\n{output}");
        string rc = File.ReadAllText(BashrcPath);
        Assert.Contains(
            RemoteShellIntegrationSnippets.GetLoaderLine(RemoteShellIntegrationShell.BashOrZsh)!,
            rc,
            StringComparison.Ordinal);
        Assert.Contains("nova: added loader line to ~/.bashrc", output, StringComparison.Ordinal);
    }

    // ---- idempotency ----------------------------------------------------------------------------

    [Fact]
    public void Installer_RunTwice_LeavesExactlyOneLoaderLine()
    {
        RunInstaller();
        string secondOutput = RunInstaller();

        Assert.Equal(1, CountLoaderLines(File.ReadAllText(BashrcPath)));
        Assert.Contains("already present", secondOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A user who followed the old docs already has the loader line. The marker is the file name
    /// rather than our exact line, so a hand-typed variant is still recognized.
    /// </summary>
    [Fact]
    public void Installer_HandPlacedLoaderLine_IsNotDuplicated()
    {
        File.WriteAllText(
            BashrcPath,
            "PS1='test$ '\nsource ~/.nova-shell-integration.sh\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        RunInstaller();

        Assert.Equal(1, CountLoaderLines(File.ReadAllText(BashrcPath)));
    }

    // ---- failure ---------------------------------------------------------------------------------

    /// <summary>
    /// With no <c>base64</c> or <c>gzip</c> reachable the decode produces an empty temp file. The
    /// one-liner must say so rather than silently install nothing, which is the failure mode a user
    /// cannot diagnose.
    /// </summary>
    [Fact]
    public void Installer_WithoutBase64OrGzip_ReportsFailureAndWritesNothing()
    {
        string emptyDir = Path.Combine(_home, "empty-path");
        Directory.CreateDirectory(emptyDir);

        string output = RunInstaller(pathOverride: emptyDir.Replace('\\', '/'));

        Assert.Contains("nova: install failed", output, StringComparison.Ordinal);
        Assert.False(File.Exists(SnippetPath), "snippet written despite a failed decode");
    }

    // ---- and afterwards, the marks flow ----------------------------------------------------------

    /// <summary>
    /// The end-to-end claim: install, then start an interactive bash that reads the rc file the
    /// installer patched, and the OSC 133 lifecycle arrives. This is what the user gets on their
    /// next session, and it is asserted through the production PTY + parser path.
    /// </summary>
    [Fact]
    public void AfterInstalling_ANewInteractiveShell_EmitsTheLifecycle()
    {
        RunInstaller();

        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        // The installer writes only the loader line; a prompt is needed for the 133;B mark to have
        // somewhere to land, so prepend one the same way RemoteBashSnippetIntegrationTests does.
        string rc = File.ReadAllText(BashrcPath);
        File.WriteAllText(
            BashrcPath,
            "PS1='nova-test$ '\n" + rc,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var env = new Dictionary<string, string> { ["HOME"] = HomeForShell };
        HarnessResult result = ShellHarness.Run(
            bash,
            $"--rcfile \"{BashrcPath.Replace('\\', '/')}\" -i",
            "echo hello\nexit 0\n",
            env,
            TimeSpan.FromSeconds(20));

        Assert.Contains(result.Events, e => e.Kind == "A");
        Assert.Contains(result.Events, e => e.Kind == "B");
        Assert.Contains(
            result.Events.Where(e => e.Kind == "C").Select(e => e.DecodedCommand),
            t => t == "echo hello");
        Assert.Contains(result.Events, e => e.Kind == "D" && e.DecodedFinish.exitCode == 0);
    }

    // ---- the live shell is untouched -------------------------------------------------------------

    /// <summary>
    /// The design's central promise: the installer runs as a child, so nothing it defines can reach
    /// the shell that pasted the line. Asserted by checking the calling shell afterwards for the
    /// installer's own variables and for the snippet's marker function.
    /// </summary>
    [Fact]
    public void Installer_LeavesNothingBehindInTheCallingShell()
    {
        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.BashOrZsh);
        string probe =
            command +
            "; echo \"probe-dest=[${__nova_dest-}]\"" +
            "; echo \"probe-temp=[${__nova_t-}]\"";

        var startInfo = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(probe);
        startInfo.Environment["HOME"] = HomeForShell;

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "probe did not finish within 30s");

        Assert.Contains("probe-dest=[]", output, StringComparison.Ordinal);
        Assert.Contains("probe-temp=[]", output, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run them to verify they fail for the right reason**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~RemoteInstallerIntegrationTests"
```

Expected: the suite compiles and runs. Any failure here is a real defect in Task 1's asset or composition — these tests need no new production code. If every test reports `bash not found`, install Git Bash or run this task on Linux; do not mark it done on skips.

- [ ] **Step 3: Fix whatever the run exposed**

Likely candidates, in the order they show up:

- `WritesTheSnippetByteForByte` fails with an empty file → the heredoc delimiter line in `nova-install.sh` has trailing whitespace, or `@@NOVA_SNIPPET@@` was indented. Both must be at column 0.
- `AddsTheLoaderLineToBashrc` says "could not tell which shell you use" → the `${BASH_VERSION:+bash}` expansion is being quoted wrong in the one-liner template; it must reach the installer as `$1`.
- `WithoutBase64OrGzip` finds a snippet anyway → Git Bash resolved `base64` outside `PATH`; assert instead on the `nova: install failed` line only, and note it in the test's remarks.

- [ ] **Step 4: Run to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~RemoteInstallerIntegrationTests"
```

Expected: 7 passed, 0 skipped on a machine with bash.

- [ ] **Step 5: Commit**

```bash
git add tests/NovaTerminal.App.Tests/CommandAssist/ShellIntegration/Integration/RemoteInstallerIntegrationTests.cs assets/shell-integration/install/nova-install.sh src/NovaTerminal.CommandAssist
git commit -m "test(command-assist): run the generated installer through a real bash"
```

---

## Task 3: The fish installer

**Files:**
- Create: `assets/shell-integration/install/nova-install-fish.sh`
- Modify: `src/NovaTerminal.CommandAssist/NovaTerminal.CommandAssist.csproj`
- Modify: `src/NovaTerminal.CommandAssist/ShellIntegration/Remote/RemoteShellIntegrationSnippets.cs` (the `switch` in `BuildInstallerCommand`)
- Test: `tests/NovaTerminal.App.Tests/CommandAssist/ShellIntegration/RemoteShellIntegrationInstallerTests.cs`, `.../Integration/RemoteInstallerIntegrationTests.cs`

**Interfaces:**
- Consumes: everything Task 1 produced.
- Produces: `BuildInstallerCommand(RemoteShellIntegrationShell.Fish)` returns a fish-syntax one-liner whose payload is a **POSIX sh** installer.

The installer is sh, not fish: fish cannot parse a heredoc, and the fish snippet it installs is data. `conf.d` is auto-sourced, so there is no rc file to patch and no shell to detect — the one-liner still passes `fish` as `$1` to keep the argv contract identical across installers, and this installer ignores it.

- [ ] **Step 1: Write the asset**

Create `assets/shell-integration/install/nova-install-fish.sh` with LF endings:

```sh
#!/bin/sh
# Nova Terminal remote shell integration installer (fish).
#
# POSIX sh, not fish: fish cannot parse a heredoc, and the snippet below is data. Run as a child
# process by the one-liner Settings copies, then deleted. $1 is the shell name ("fish"), accepted
# for symmetry with nova-install.sh and unused - conf.d is sourced automatically, so there is no
# rc file to patch and no shell to detect.

__nova_dir="$HOME/.config/fish/conf.d"
if ! mkdir -p "$__nova_dir"; then
    echo "nova: could not create $__nova_dir"
    exit 1
fi

__nova_dest="$__nova_dir/nova-shell-integration.fish"

cat > "$__nova_dest" <<'__NOVA_SNIPPET_EOF__'
@@NOVA_SNIPPET@@
__NOVA_SNIPPET_EOF__

if [ ! -s "$__nova_dest" ]; then
    echo "nova: could not write $__nova_dest"
    exit 1
fi

echo "nova: wrote ~/.config/fish/conf.d/nova-shell-integration.fish"
echo "nova: conf.d is sourced automatically - there is nothing to add to a config file."
echo "nova: run  source ~/.config/fish/conf.d/nova-shell-integration.fish  to enable it in this session,"
echo "nova: or open a new Nova session to this host."
```

- [ ] **Step 2: Embed it**

Add to the same `ItemGroup`:

```xml
    <EmbeddedResource Include="$(MSBuildThisFileDirectory)..\..\assets\shell-integration\install\nova-install-fish.sh"
                      LogicalName="NovaTerminal.CommandAssist.ShellIntegration.Remote.nova-install-fish.sh" />
```

- [ ] **Step 3: Write the failing tests**

Add to `RemoteShellIntegrationInstallerTests`:

```csharp
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
```

Also extend the delimiter theory added in Task 1 Step 8 with `[InlineData(RemoteShellIntegrationShell.Fish)]`.

And add to `RemoteInstallerIntegrationTests` — the fish *installer* is sh, so bash can run it even where fish is absent, which is what makes this testable on Windows:

```csharp
    /// <summary>
    /// The fish installer is POSIX sh, so it is exercised under bash: the payload it writes is fish
    /// content, but nothing about running the installer needs fish present. That the fish snippet
    /// itself works is FishShellIntegrationTests' job.
    /// </summary>
    [Fact]
    public void FishInstaller_WritesTheSnippetIntoConfD()
    {
        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        // Run the fish installer's payload directly: the fish one-liner is fish syntax, which bash
        // cannot parse, and what is under test here is the installer it decodes to.
        string installer = RemoteShellIntegrationSnippets.BuildInstallerScript(
            RemoteShellIntegrationShell.Fish);
        string installerPath = Path.Combine(_home, "nova-install-fish.sh");
        File.WriteAllText(installerPath, installer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(installerPath.Replace('\\', '/'));
        startInfo.ArgumentList.Add("fish");
        startInfo.Environment["HOME"] = HomeForShell;

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "fish installer did not finish within 30s");

        string dest = Path.Combine(_home, ".config", "fish", "conf.d", "nova-shell-integration.fish");
        Assert.True(File.Exists(dest), $"fish snippet not written. output:\n{output}");
        Assert.Equal(
            RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish).TrimEnd('\n'),
            File.ReadAllText(dest).Replace("\r\n", "\n").TrimEnd('\n'));
    }
```

- [ ] **Step 4: Run to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Installer"
```

Expected: the four new static tests fail with `ArgumentOutOfRangeException: No installer ships for this shell.`

- [ ] **Step 5: Add the fish branch**

In `BuildInstallerCommand`'s `switch`, before the `_ =>` arm:

```csharp
            RemoteShellIntegrationShell.Fish =>
                """
                set -l __nova_t (mktemp); printf %s '@@BLOB@@' | base64 -d | gzip -dc > $__nova_t; sh $__nova_t fish; rm -f $__nova_t; set -e __nova_t
                """,
```

- [ ] **Step 6: Run to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Installer"
```

Expected: all pass — 12 static, 8 integration.

- [ ] **Step 7: Commit**

```bash
git add assets/shell-integration/install/nova-install-fish.sh src/NovaTerminal.CommandAssist tests/NovaTerminal.App.Tests
git commit -m "feat(command-assist): one-line installer for the fish remote snippet"
```

---

## Task 4: The PowerShell installer

**Files:**
- Create: `assets/shell-integration/install/nova-install.ps1`
- Modify: `src/NovaTerminal.CommandAssist/NovaTerminal.CommandAssist.csproj`
- Modify: `src/NovaTerminal.CommandAssist/ShellIntegration/Remote/RemoteShellIntegrationSnippets.cs` (`BuildInstallerScript` delimiter selection, `BuildInstallerCommand` switch)
- Test: both test files from Tasks 1–3

**Interfaces:**
- Consumes: everything Tasks 1–3 produced.
- Produces: `BuildInstallerCommand(RemoteShellIntegrationShell.PowerShell)`; `BuildInstallerScript` now selects its delimiter per shell — `'@` for PowerShell (the here-string terminator), `__NOVA_SNIPPET_EOF__` otherwise.

Two shell-specific points. `& $t` rather than `. $t`: the call operator runs the installer in a child scope, so nothing it defines leaks into the session, and `$PROFILE` is still visible because it is an automatic variable in every scope. And the writes go through `[IO.File]::WriteAllText` with an explicit no-BOM UTF-8 rather than `Set-Content -Encoding utf8NoBOM`, because that parameter value does not exist on Windows PowerShell 5.1, which a remote host may well be running.

- [ ] **Step 1: Write the asset**

Create `assets/shell-integration/install/nova-install.ps1` with LF endings:

```powershell
# Nova Terminal remote shell integration installer (PowerShell).
#
# Decoded to a temp file by the one-liner Settings copies, invoked with the call operator (& ) so it
# runs in a CHILD SCOPE - nothing it defines reaches your session - and then deleted. $PROFILE is
# still visible here because it is an automatic variable in every scope.
#
# The parameters exist so this file is testable without touching the developer's real profile; the
# generated one-liner passes none of them.

param(
    [string]$ProfilePath = $PROFILE,
    [string]$DestDir = $HOME
)

$dest = Join-Path $DestDir '.nova-shell-integration.ps1'
$snippet = @'
@@NOVA_SNIPPET@@
'@

# WriteAllText with an explicit no-BOM UTF-8 rather than Set-Content -Encoding utf8NoBOM: that
# parameter value does not exist on Windows PowerShell 5.1, and a remote host may be running it.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($dest, $snippet, $utf8NoBom)

if (-not (Test-Path -LiteralPath $dest)) {
    Write-Host "nova: could not write $dest"
    exit 1
}
Write-Host 'nova: wrote ~/.nova-shell-integration.ps1'

$loader = '. ~/.nova-shell-integration.ps1'
$profileDir = Split-Path -Parent $ProfilePath
if ($profileDir -and -not (Test-Path -LiteralPath $profileDir)) {
    New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
}

if ((Test-Path -LiteralPath $ProfilePath) -and
    (Select-String -LiteralPath $ProfilePath -SimpleMatch 'nova-shell-integration' -Quiet)) {
    Write-Host 'nova: loader line already in $PROFILE - unchanged'
} else {
    Add-Content -LiteralPath $ProfilePath -Value $loader
    Write-Host 'nova: added loader line to $PROFILE'
}

Write-Host 'nova: run  . ~/.nova-shell-integration.ps1  to enable it in this session,'
Write-Host 'nova: or open a new Nova session to this host.'
```

The `Write-Host` strings are single-quoted deliberately: `$PROFILE` is meant to print literally, as the name of the file, not expand to a path.

- [ ] **Step 2: Embed it**

```xml
    <EmbeddedResource Include="$(MSBuildThisFileDirectory)..\..\assets\shell-integration\install\nova-install.ps1"
                      LogicalName="NovaTerminal.CommandAssist.ShellIntegration.Remote.nova-install.ps1" />
```

- [ ] **Step 3: Write the failing tests**

Add to `RemoteShellIntegrationInstallerTests`:

```csharp
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
        string payload = Regex.Match(command, @"FromBase64String\('([^']*)'\)").Groups[1].Value;

        Assert.Matches("^[A-Za-z0-9+/=]+$", payload);
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
```

Add to `RemoteInstallerIntegrationTests`:

```csharp
    /// <summary>
    /// The PowerShell installer, run by a real pwsh with both of its parameters redirected into the
    /// temp HOME. The parameters are the only reason this is testable: <c>$PROFILE</c> resolves
    /// under the developer's Documents directory and cannot be redirected by an environment variable.
    /// </summary>
    [Fact]
    public void PowerShellInstaller_WritesTheSnippetAndPatchesTheProfileOnce()
    {
        string? pwsh = FindPwsh();
        if (pwsh is null)
        {
            Assert.Skip("pwsh not found on this system");
        }

        string installerPath = Path.Combine(_home, "nova-install.ps1");
        File.WriteAllText(
            installerPath,
            RemoteShellIntegrationSnippets.BuildInstallerScript(
                RemoteShellIntegrationShell.PowerShell),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string profilePath = Path.Combine(_home, "profile.ps1");
        string output = RunPwsh(pwsh, installerPath, profilePath) + RunPwsh(pwsh, installerPath, profilePath);

        string dest = Path.Combine(_home, ".nova-shell-integration.ps1");
        Assert.True(File.Exists(dest), $"snippet not written. output:\n{output}");
        Assert.Equal(
            RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell).TrimEnd('\n'),
            File.ReadAllText(dest).Replace("\r\n", "\n").TrimEnd('\n'));
        Assert.Equal(1, CountLoaderLines(File.ReadAllText(profilePath)));
        Assert.Contains("already present", output, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindPwsh()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null) return null;
        string exe = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private string RunPwsh(string pwsh, string installerPath, string profilePath)
    {
        var startInfo = new ProcessStartInfo(pwsh)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("-ProfilePath");
        startInfo.ArgumentList.Add(profilePath);
        startInfo.ArgumentList.Add("-DestDir");
        startInfo.ArgumentList.Add(_home);

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "pwsh installer did not finish within 60s");
        return output;
    }
```

- [ ] **Step 4: Run to verify they fail**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Installer"
```

Expected: the PowerShell static tests fail with `ArgumentOutOfRangeException: No installer ships for this shell.`

- [ ] **Step 5: Make the delimiter shell-specific**

In the two-argument `BuildInstallerScript(shell, snippet)` overload, replace the `const string Delimiter` line and the loop's message with a per-shell delimiter. Task 4 Step 3's `PowerShellSnippet_DoesNotCollideWithTheHereStringTerminator` covers the shipped asset; the throw path is already covered for the sh delimiter by Task 1's guard test:

```csharp
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
```

- [ ] **Step 6: Add the PowerShell branch**

In `BuildInstallerCommand`'s `switch`:

```csharp
            RemoteShellIntegrationShell.PowerShell =>
                """
                $__nova_t=[IO.Path]::GetTempPath()+[Guid]::NewGuid().ToString('N')+'.ps1'; $__nova_g=[IO.Compression.GZipStream]::new([IO.MemoryStream]::new([Convert]::FromBase64String('@@BLOB@@')),[IO.Compression.CompressionMode]::Decompress); $__nova_o=[IO.File]::Create($__nova_t); $__nova_g.CopyTo($__nova_o); $__nova_o.Dispose(); $__nova_g.Dispose(); & $__nova_t; Remove-Item $__nova_t; Remove-Variable __nova_t,__nova_g,__nova_o
                """,
```

- [ ] **Step 7: Run to verify they pass**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~Installer"
```

Expected: all pass — 17 static, 9 integration. `PowerShellInstaller_WritesTheSnippetAndPatchesTheProfileOnce` skips only if `pwsh` is not on `PATH`; on this machine it should run.

- [ ] **Step 8: Commit**

```bash
git add assets/shell-integration/install/nova-install.ps1 src/NovaTerminal.CommandAssist tests/NovaTerminal.App.Tests
git commit -m "feat(command-assist): one-line installer for the PowerShell remote snippet"
```

---

## Task 5: Settings row and docs

**Files:**
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml:628-638`
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml.cs:1447-1498`
- Modify: `docs/command-assist/RemoteShellIntegration.md:30-75`

**Interfaces:**
- Consumes: `RemoteShellIntegrationSnippets.BuildInstallerCommand(shell)`, `Read(shell)`, `BuildInstallInstructions(shell)`, `GetDisplayName(shell)`, `GetRemotePath(shell)`, `All`.
- Produces: nothing consumed by later tasks. This is the last task.

Docs ship with the UI in one task on purpose: the row's copy and the docs' Install section describe the same two buttons, and splitting them guarantees a window where they disagree.

- [ ] **Step 1: Update the XAML**

In `src/NovaTerminal.App/SettingsWindow.axaml`, replace the row description text at line 631 and the button panel at lines 634–637:

```xml
                                    <TextBlock Classes="RowDesc" TextWrapping="Wrap" Text="Nova cannot install shell integration over SSH. Copy the one-line installer for the remote shell, paste it at the prompt on that host, and the command assistant works there too: history, suggestions read from the prompt line, exit codes and prompt-anchored placement. Filesystem path suggestions stay off for remote sessions - they would list the local disk."/>
```

```xml
                                <StackPanel Grid.Column="1" Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center" Spacing="6">
                                    <ComboBox Name="RemoteShellIntegrationShellList" MinWidth="150"/>
                                    <Button Name="BtnCopyRemoteShellIntegration" Classes="Pill" Content="Copy installer"/>
                                    <Button Name="BtnCopyRemoteShellIntegrationSnippet" Classes="Flat" Content="Copy plain snippet"/>
                                </StackPanel>
```

Check that a `Flat` button style exists in this window's styles before using it — search the file for `Selector="Button.Flat"`. If there is none, use `Classes="Pill"` for the secondary button too rather than inventing a style in this task.

- [ ] **Step 2: Wire both buttons**

In `SettingsWindow.axaml.cs`, `WireRemoteShellIntegrationRow` (line 1447), keep the picker setup and replace the single `Click` handler with two. Add a shared selection helper so the two handlers cannot drift:

```csharp
        private void WireRemoteShellIntegrationRow()
        {
            var shellList = this.FindControl<ComboBox>("RemoteShellIntegrationShellList");
            var copyInstallerButton = this.FindControl<Button>("BtnCopyRemoteShellIntegration");
            var copySnippetButton = this.FindControl<Button>("BtnCopyRemoteShellIntegrationSnippet");
            var status = this.FindControl<TextBlock>("RemoteShellIntegrationStatus");
            if (shellList == null || copyInstallerButton == null)
            {
                return;
            }

            shellList.ItemsSource = RemoteShellIntegrationSnippets.All
                .Select(RemoteShellIntegrationSnippets.GetDisplayName)
                .ToList();
            shellList.SelectedIndex = 0;

            RemoteShellIntegrationShell SelectedShell()
            {
                int index = Math.Clamp(
                    shellList.SelectedIndex,
                    0,
                    RemoteShellIntegrationSnippets.All.Count - 1);
                return RemoteShellIntegrationSnippets.All[index];
            }

            copyInstallerButton.Click += async (_, _) =>
                await CopyRemoteShellIntegrationInstallerAsync(SelectedShell(), status);

            if (copySnippetButton != null)
            {
                copySnippetButton.Click += async (_, _) =>
                    await CopyRemoteShellIntegrationSnippetAsync(SelectedShell(), status);
            }
        }
```

- [ ] **Step 3: Add the installer copy handler**

Immediately above the existing `CopyRemoteShellIntegrationSnippetAsync` (line 1474), add:

```csharp
        /// <summary>
        /// The primary action: one line the user pastes at the remote prompt.
        /// </summary>
        /// <remarks>
        /// The status text describes what the paste does rather than what the user must do next,
        /// because after this change there is no next step - the installer writes the snippet and
        /// patches the rc file itself. It deliberately does not promise the current session becomes
        /// integrated: the installer runs as a child process and never touches the live shell, so
        /// marks arrive with the next session.
        /// </remarks>
        private async System.Threading.Tasks.Task CopyRemoteShellIntegrationInstallerAsync(
            RemoteShellIntegrationShell shell,
            TextBlock? status)
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null)
                {
                    ShowRemoteShellIntegrationStatus(status, "Clipboard is not available.");
                    return;
                }

                await clipboard.SetTextAsync(RemoteShellIntegrationSnippets.BuildInstallerCommand(shell));
                ShowRemoteShellIntegrationStatus(
                    status,
                    $"Copied the installer for {RemoteShellIntegrationSnippets.GetDisplayName(shell)}. " +
                    "Paste it at the remote prompt and press Enter - one line, one history entry. It writes " +
                    $"{RemoteShellIntegrationSnippets.GetRemotePath(shell)} and adds the loader line to your " +
                    "config file if it isn't already there.");
            }
            catch (Exception ex)
            {
                // Reported in the row rather than swallowed: the whole point of the affordance is
                // that the user now has the installer, and silently not having it looks identical.
                ShowRemoteShellIntegrationStatus(status, $"Could not copy the installer: {ex.Message}");
            }
        }
```

Leave `CopyRemoteShellIntegrationSnippetAsync` as it is — it is now the secondary path and its `BuildInstallInstructions` text is still exactly right for it.

- [ ] **Step 4: Build and check for warnings**

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Expected: build succeeded, no new warnings. A `CS0168`/unused-variable warning means the `copySnippetButton` null branch was dropped.

- [ ] **Step 5: Manually smoke-test the row**

Automated GUI verification is unreliable here, so verify by hand:

```bash
scripts/build.ps1 build src/NovaTerminal.App
```

Then run the app, open Settings → **Command assistant**, scroll to **Remote shell integration**, and confirm:

1. The picker still offers `bash / zsh`, `fish`, `PowerShell`.
2. **Copy installer** with `bash / zsh` selected → the status line names bash / zsh and `~/.nova-shell-integration.sh`; the clipboard holds **one line** starting `__nova_t=$(mktemp` (paste it into a text editor to confirm there is no second line).
3. Switch to `PowerShell`, press **Copy installer** → clipboard starts `$__nova_t=[IO.Path]::GetTempPath()`.
4. **Copy plain snippet** → clipboard holds the full multi-line snippet and the status line reverts to the `cat >` instructions.
5. Paste the bash/zsh installer into a real SSH session to a Linux host and confirm the four `nova:` lines, then open a new session to that host and confirm the assistant reports the session as integrated.

Record the result of step 5 in the commit message. If no remote host is available, say so explicitly rather than claiming it passed.

- [ ] **Step 6: Rewrite the docs' Install section**

In `docs/command-assist/RemoteShellIntegration.md`, replace the § Install block (lines 30–75, from `Settings → **Command assistant**` through the `$PROFILE`'s-directory note) with:

```markdown
## Install

Settings → **Command assistant** → **Remote shell integration**: pick the remote shell, press
**Copy installer**, paste the one line at the prompt on that host, press Enter. It prints what it
did:

```
nova: wrote ~/.nova-shell-integration.sh
nova: added loader line to ~/.zshrc
nova: run  . ~/.nova-shell-integration.sh  to enable it in this session,
nova: or open a new Nova session to this host.
```

One line and one history entry, rather than the 300-line paste this replaced. The line decodes a
gzipped copy of the snippet into a temp file, runs it as a **child process**, and deletes it. It
never sources anything into your live shell: the shell's identity is expanded by your shell and
handed to the installer as an argument, so it knows whether to patch `~/.bashrc` or `~/.zshrc`
without touching your session. Running it twice changes nothing the second time — the loader line is
added only if it isn't already there, including when you placed it by hand.

Integration starts with your next session to that host. The third line above is there if you want it
sooner in the shell you pasted into.

fish needs no loader line at all: its snippet goes to `~/.config/fish/conf.d/`, which fish sources
automatically.

### Placing the file yourself

**Copy plain snippet** puts the whole file on the clipboard instead, and the row tells you where it
goes. That is the path for a dotfiles repo, `/etc/profile.d`, or reading the snippet before you
trust it — the installers themselves are readable files in the repository under
`assets/shell-integration/install/`.
```

- [ ] **Step 7: Update the class remark**

In `RemoteShellIntegrationSnippets.cs`, the `BuildInstallInstructions` remark still argues against a generated installer ("a base64 blob, which the user cannot read before running"). Replace that paragraph with the reversal, and keep the `cat > path` paragraph — it documents the secondary path, which still works that way:

```csharp
    /// <para>
    /// This is the secondary path. The primary one is <see cref="BuildInstallerCommand"/>, and these
    /// instructions belong to the "Copy plain snippet" action beside it.
    /// </para>
    /// <para>
    /// The class used to argue against a generated installer on the grounds that a base64 blob
    /// cannot be read before it is run. That objection was real and is now met differently: the
    /// installers are reviewable files under <c>assets/shell-integration/install/</c>, and this
    /// readable path is still one click away in the same row. What the 300-line paste cost was not
    /// worth keeping - a flooded scrollback, a slow paste in any shell with syntax highlighting, an
    /// rc edit left to the user, and a PowerShell recipe that could not work on a Windows remote at
    /// all, where <c>cat</c> is an alias for <c>Get-Content</c> and so <c>cat &gt; file</c> has no
    /// input to read.
    /// </para>
```

- [ ] **Step 8: Run the full installer and snippet suites once more**

```bash
scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~RemoteShellIntegration|FullyQualifiedName~RemoteInstaller|FullyQualifiedName~RemoteBashSnippet"
```

Expected: all pass. The pre-existing `RemoteShellIntegrationSnippetTests` and `RemoteBashSnippetIntegrationTests` must be untouched by this work — a failure there means a snippet or descriptor was edited when it should not have been.

- [ ] **Step 9: Commit**

```bash
git add src/NovaTerminal.App/SettingsWindow.axaml src/NovaTerminal.App/SettingsWindow.axaml.cs src/NovaTerminal.CommandAssist docs/command-assist/RemoteShellIntegration.md
git commit -m "feat(command-assist): Settings copies a one-line remote installer, plain snippet secondary"
```

---

## Done when

- `BuildInstallerCommand` returns a single line for all three shells, and its payload round-trips to the installer script byte-for-byte.
- The generated bash/zsh command, run through a real bash, writes the snippet, patches `~/.bashrc` exactly once across two runs, reports a hand-placed loader line as already present, reports failure when `base64`/`gzip` are unreachable, leaves nothing in the calling shell, and yields a shell that emits the full OSC 133 lifecycle afterwards.
- The fish installer writes into `conf.d`; the PowerShell installer writes the snippet and patches its profile once across two runs.
- Settings offers **Copy installer** and **Copy plain snippet**, manually verified, including one real paste into a remote host.
- `docs/command-assist/RemoteShellIntegration.md` documents the new flow and no longer contains the `cat > ~/.nova-shell-integration.ps1` recipe.
