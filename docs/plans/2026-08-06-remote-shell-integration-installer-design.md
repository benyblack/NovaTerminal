# Remote shell integration: one-line installer

Settings → **Command assistant** → **Remote shell integration** hands the user the whole snippet and
a `cat >` recipe. This replaces that with a single line the user pastes at the remote prompt.

Written to `docs/plans/` rather than `docs/superpowers/specs/` to sit next to the rest of the
Command Assist V2 documents.

## The problem

Today's flow: pick the shell, press **Copy snippet**, and the clipboard receives the entire file —
295 lines / 12.7 KB for bash and zsh, 194 lines for PowerShell. The row then tells the user to run
`cat > ~/.nova-shell-integration.sh`, paste, press Ctrl-D, and afterwards append a loader line to
their rc file by hand.

Four things are wrong with it:

- **The paste is enormous.** 300 lines echoed back over SSH floods the scrollback and is slow to
  redraw in any shell with syntax highlighting or autosuggestions.
- **It pollutes history when the user skips `cat`.** A `cat >` heredoc keeps the paste out of
  history, but a user who pastes the snippet straight at the prompt — the obvious thing to try —
  gets a few hundred history entries. PowerShell has no `cat >` equivalent at all, so its snippet
  lands in `ConsoleHost_history.txt` in full.
- **The PowerShell recipe does not work on a Windows remote.** `cat` there is an alias for
  `Get-Content`, so `cat > ~/.nova-shell-integration.ps1` fails with a missing `-Path` argument
  rather than reading stdin.
- **The rc edit is a separate manual step**, and it is the step people forget. The symptom — "I
  installed it and nothing happened" — is already in the troubleshooting section.

## The shape

One line, one history entry, roughly four lines of output. The line writes a temp file, runs it as
a **child process**, and deletes it:

```
nova: wrote ~/.nova-shell-integration.sh
nova: added loader line to ~/.zshrc
nova: run  . ~/.nova-shell-integration.sh  to enable it here now,
nova: or open a new Nova session to this host.
```

### Composition

`RemoteShellIntegrationSnippets` gains `BuildInstallerCommand(RemoteShellIntegrationShell)`
returning that single line. All of it happens at copy time:

1. Read the shell's snippet and its installer template from embedded resources, LF-normalized as
   `Read` already does.
2. Substitute the snippet into the template's `@@NOVA_SNIPPET@@` line. Throw if the snippet
   contains a line that collides with the template's heredoc / here-string delimiter — today none
   do (no line matches `^__NOVA`, `^'@` or `^NOVA_EOF`), and a silent collision would emit a
   corrupt installer.
3. gzip, then base64 with no line breaks.
4. Wrap in the per-shell prologue below.

Base64's alphabet is `[A-Za-z0-9+/=]`, which contains no shell metacharacter. The blob therefore
needs no escaping in any of the three shells, and the design has no quoting logic to get wrong.

Payload sizes, measured against the shipped assets:

| Snippet | Raw | base64 | gzip + base64 |
|---|---|---|---|
| `nova-shell-integration.sh` | 12.7 KB | 17.0 KB | **6.4 KB** |
| `nova-shell-integration.ps1` | 10.3 KB | 13.8 KB | **5.7 KB** |
| `nova-shell-integration.fish` | 4.8 KB | 6.4 KB | **2.7 KB** |

gzip and base64 are present on effectively every Unix host, busybox included, and PowerShell needs
neither — it has `[Convert]::FromBase64String` and `GZipStream` in the box. The compression is worth
its dependency: a 17 KB single line is slow to paste and redraw, and is the case most likely to trip
a flaky link.

`Read` and `BuildInstallInstructions` stay as they are. They back the secondary **Copy plain
snippet** action.

### New assets

Under `assets/shell-integration/install/`, embedded into `NovaTerminal.CommandAssist` the same way
the snippets are:

| File | Runs under | Target |
|---|---|---|
| `nova-install.sh` | `sh` | bash and zsh |
| `nova-install-fish.sh` | `sh` | fish |
| `nova-install.ps1` | pwsh child scope | PowerShell |

The installer logic lives in reviewable repo files rather than C# string literals, which is also
what makes it directly testable.

fish's installer is POSIX sh, not fish: fish cannot parse a heredoc, and the snippet it installs is
data, so there is nothing gained by writing the installer in the target shell's language.

### The three one-liners

**bash / zsh.** The shell's identity is expanded by the live shell and passed to the child as `$1`,
which is how the installer knows which rc file to patch without anything being sourced:

```sh
__nova_t=$(mktemp 2>/dev/null || printf /tmp/nova-si.%s "$$"); printf %s 'H4sI…6.4KB…' | base64 -d 2>/dev/null | gzip -dc 2>/dev/null > "$__nova_t"; if [ -s "$__nova_t" ]; then sh "$__nova_t" "${ZSH_VERSION:+zsh}${BASH_VERSION:+bash}"; else echo "nova: install failed - this host needs base64 and gzip"; fi; rm -f "$__nova_t"; unset __nova_t
```

**fish.** No rc file to patch — `conf.d` is auto-sourced — so the shell argument is a constant:

```fish
set -l __nova_t (mktemp); printf %s 'H4sI…2.7KB…' | base64 -d | gzip -dc > $__nova_t; sh $__nova_t fish; rm -f $__nova_t; set -e __nova_t
```

**PowerShell.** Pure .NET, no external tools. `& $t` rather than `. $t`: the call operator runs the
installer in a child scope, so nothing it defines leaks into the session, and `$PROFILE` is still
visible because it is an automatic variable in every scope:

```powershell
$__nova_t=[IO.Path]::GetTempPath()+[Guid]::NewGuid().ToString('N')+'.ps1'; $__nova_g=[IO.Compression.GZipStream]::new([IO.MemoryStream]::new([Convert]::FromBase64String('H4sI…5.7KB…')),[IO.Compression.CompressionMode]::Decompress); $__nova_o=[IO.File]::Create($__nova_t); $__nova_g.CopyTo($__nova_o); $__nova_o.Dispose(); $__nova_g.Dispose(); & $__nova_t; Remove-Item $__nova_t; Remove-Variable __nova_t,__nova_g,__nova_o
```

### What the installer does

Four steps, one `nova:` line each:

1. **Write the snippet** to `~/.nova-shell-integration.sh`, `~/.nova-shell-integration.ps1`, or
   `~/.config/fish/conf.d/nova-shell-integration.fish`, creating `conf.d` or `$PROFILE`'s directory
   if it does not exist. The paths are the ones `GetRemotePath` already reports.
2. **Resolve the rc file** from `$1` (`zsh` → `~/.zshrc`, `bash` → `~/.bashrc`), falling back to
   `basename "$SHELL"`. When neither answers, print the loader line and the fact that it could not
   tell which rc file to use, rather than guessing. PowerShell uses `$PROFILE`; fish has no rc step.
3. **Patch the rc idempotently**: append `GetLoaderLine`'s text only when a `nova-shell-integration`
   match is not already present, and report `added` against `already present — unchanged`. Running
   the installer twice writes the loader line once, and a line the user placed by hand is not
   duplicated.
4. **Print how to enable it in the current session** — `. ~/.nova-shell-integration.sh` — and that
   a new session picks it up on its own.

### Why the live shell is never touched

An earlier draft sourced the installer, so it could read `$BASH_VERSION` / `$ZSH_VERSION` directly
and then source the snippet to make marks live immediately. Both are given up deliberately.

Grafting the prompt wrapper onto a *running* prompt is the one case the snippets' load guards exist
for — a framework that rebuilds its hook chain after us can defeat them — and it is avoidable at
almost no cost. Detection does not need sourcing, because the visible part of the one-liner already
runs in the live shell and can expand the answer into the child's argv. What is genuinely lost is
marks-in-this-session, and that matches what the docs already promise ("Marks only take effect on a
*new* session"); step 4 hands over the one-line command for a user who wants it sooner.

The residual contact with the live shell is one variable, `__nova_t`, unset on the same line.

## Settings row

`SettingsWindow.axaml:628` keeps `RemoteShellIntegrationShellList` unchanged. The buttons become:

| Control | Behaviour |
|---|---|
| **Copy installer** (primary, `Classes="Pill"`) | `BuildInstallerCommand(shell)` to the clipboard |
| **Copy plain snippet** (secondary, flat) | today's behaviour: `Read(shell)` plus `BuildInstallInstructions(shell)` in the status line |

The plain path is kept, not merely for symmetry: it is how a user places the file in a dotfiles
repo or `/etc/profile.d`, and it is the auditable option for someone who wants to read the snippet
before trusting it. Keeping it is also what answers the objection the current code records against
generated installers — the argument was that a base64 blob cannot be read before it runs, and the
answer is that the installer is a reviewable asset in the repository and the readable path is still
one click away.

Status text after **Copy installer**:

> Copied the installer for bash / zsh. Paste it at the remote prompt and press Enter — one line, one
> history entry. It writes `~/.nova-shell-integration.sh` and adds the loader line to your rc file
> if it isn't already there.

The row description drops "Copy the snippet for the remote shell, paste it on the host" for "Copy
the one-line installer and paste it at the remote prompt." No new `TerminalSettings` field, so
`TerminalPane.ApplySettings`'s whitelist is not involved.

## Tests

Static, in `RemoteShellIntegrationSnippetTests`:

- the command is exactly one line — no `\n`, no `\r` — for all three shells
- the base64 argument is single-quoted and matches `^[A-Za-z0-9+/=]+$`, the property the
  no-escaping design rests on
- round trip: base64-decode, gunzip, and the snippet inside the installer is byte-for-byte
  `Read(shell)`
- the delimiter-collision guard throws when handed a snippet containing the template's delimiter,
  rather than emitting a corrupt installer
- the paths and loader lines in the generated command are the ones `GetRemotePath` and
  `GetLoaderLine` report, so the row, the docs and the installer cannot drift

Behavioural, alongside `RemoteBashSnippetIntegrationTests`, using the existing
`ShellHarness.FindBash()` + `Assert.Skip` pattern with `HOME` redirected to a per-test temp
directory:

- a paste-equivalent run writes the snippet byte-for-byte and adds exactly one loader line to
  `~/.bashrc`; a shell started afterwards against that rc emits marks
- running it twice leaves exactly one loader line, and the second run reports `already present`
- a loader line placed by hand beforehand is not duplicated
- with `gzip` removed from `PATH`, the failure branch prints the `needs base64 and gzip` message and
  writes nothing
- the calling shell is unchanged: no `nova` function or variable defined in it afterwards, and no
  prompt change

`[Trait("Category", "ShellIntegration")]` per the existing quarantine convention. No new test
project, so `ci.yml` needs no changes.

## Docs

`docs/command-assist/RemoteShellIntegration.md` § Install: the three `cat >` recipes collapse to
"pick the shell, press **Copy installer**, paste, Enter", with the plain-snippet path kept as a
short "if you would rather place the file yourself" note. The broken PowerShell `cat >` recipe goes
away. The class remark in `RemoteShellIntegrationSnippets` that argues against a generated installer
is rewritten to record why it was reversed rather than deleted.

## Risks accepted

- **`mktemp`-less hosts** fall back to `/tmp/nova-si.$$`, a predictable path a local attacker on
  the remote host could pre-plant as a symlink. `mktemp` is present anywhere this realistically
  runs, and today's `cat > ~/...` has no better property.
- **A 6.4 KB single line** is still a large paste. It is 2.6× smaller than the uncompressed blob,
  half the bytes of today's snippet paste, and — the point — one line and one history entry instead
  of 300 lines.
