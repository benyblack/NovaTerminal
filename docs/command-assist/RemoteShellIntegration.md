# Remote shell integration (SSH)

Command Assist reads the OSC 133 marks a shell emits: `A` (prompt start), `B` (prompt end — the
first cell of your input), `C` (the line you submitted), `D` (exit code and duration), plus OSC 7
for the working directory. On a local session Nova installs the emitter for you. Over SSH it
cannot: every injection mechanism it has — a `bash --rcfile` path, a `ZDOTDIR` or
`XDG_CONFIG_HOME` override, a pwsh `-File` bootstrap — dies at the SSH boundary.

So you install it on the remote host once, and from then on that host's sessions behave like local
integrated ones.

## What you get

| | Un-instrumented SSH | Instrumented SSH |
|---|---|---|
| History capture | typed-straight-through lines only, heuristic | every command, from the `133;C` payload, tagged `ShellIntegration` |
| Exit codes and durations in history | no | yes, from `133;D` |
| Suggestions while you type | none (there is no readable command line) | yes, read from the grid between `B` and the cursor |
| `Ctrl+Enter` insertion into the command line | refused | yes |
| Fix mode after a failure | command text only when the heuristic caught it | always |
| Overlay placement | conservative band heuristic, suppressed on short panes | anchored to the `133;B` mark |
| Filesystem path suggestions | off | **still off** — see below |

Path suggestions stay off for every remote session, instrumented or not.
`FileSystemPathSuggestionProvider` completes against the machine Nova is running on, and over SSH
that is the wrong filesystem: it would offer your laptop's directories at a prompt sitting on the
server. Completing the remote filesystem needs a remote listing channel, which is the remote-files
sidebar's problem and not this one.

## Install

Settings → **Command assistant** → **Remote shell integration**: pick the remote shell, press
**Copy snippet**. The whole snippet goes on your clipboard; the row then tells you the two commands
to run. The snippet repeats the same instructions in its own header comment, so they survive the
trip to the remote host.

### bash and zsh

One file covers both; it dispatches on `$BASH_VERSION` / `$ZSH_VERSION` when it loads.

```sh
cat > ~/.nova-shell-integration.sh
# ...paste, then press Ctrl-D...

# bash:
echo '[ -f ~/.nova-shell-integration.sh ] && . ~/.nova-shell-integration.sh' >> ~/.bashrc
# zsh:
echo '[ -f ~/.nova-shell-integration.sh ] && . ~/.nova-shell-integration.sh' >> ~/.zshrc
```

Then open a new Nova session to that host.

### fish

fish is not POSIX sh — `case`, `$-`, `local`, function and array syntax all differ — so it gets its
own file rather than a branch in the sh one.

```fish
mkdir -p ~/.config/fish/conf.d
cat > ~/.config/fish/conf.d/nova-shell-integration.fish
# ...paste, then press Ctrl-D...
```

`conf.d` is auto-sourced, so there is no loader line to add.

### PowerShell

```powershell
cat > ~/.nova-shell-integration.ps1
# ...paste, then press Ctrl-D...
Add-Content $PROFILE '. ~/.nova-shell-integration.ps1'
```

`$PROFILE`'s directory may not exist yet:
`New-Item -ItemType Directory -Force -Path (Split-Path $PROFILE)`.

## What the snippets promise

Everything the local bootstrap builders promise, for the same reasons, with the guards ported
across:

- **Your prompt is appended to, never replaced.** bash gets `\[\e]133;B\a\]` on the tail of `PS1`,
  re-applied every prompt cycle from the last entry in the `PROMPT_COMMAND` chain so a theme that
  rewrites `PS1` there cannot drop it. zsh gets a `%{...%}`-wrapped suffix on `PROMPT`, stripped and
  re-appended each `precmd` so a hook registered after ours cannot bury it mid-prompt. fish's
  `fish_prompt` is copied aside and wrapped. PowerShell's `prompt` is captured once and called from
  a wrapper.
- **Sourcing twice is a no-op.** A load guard covers the ordinary case; the bash `PROMPT_COMMAND`
  chain, the zsh hook arrays, the fish `fish_prompt` copy and the pwsh `prompt` capture each carry
  their own not-already-wrapped guard as well, because a framework that rebuilds one of those after
  we install can defeat the load guard alone.
- **Non-interactive shells emit nothing.** An OSC written into an `scp` or `rsync` stream corrupts
  the transfer, so the sh and fish snippets bail before defining anything when the shell is not
  interactive.
- **The bash `C` payload is the whole line.** `$BASH_COMMAND` in a `DEBUG` trap is the first
  *simple command*, not the line the user submitted: on `true && false` it is `true`, which would be
  recorded next to the other branch's exit code. The snippet reads the line back out of `history 1`
  (bash-preexec's approach) and falls back to `$BASH_COMMAND` only where history cannot answer —
  `set +o history`, or a leading-space command swallowed by `HISTCONTROL=ignorespace`.
- **Missing pieces degrade rather than fail.** No `EPOCHREALTIME` means second-resolution durations
  instead of milliseconds. No PSReadLine is the one lossy case: the Enter chord is where the pwsh
  snippet emits `133;C` *and* starts the clock, so without it you get OSC 7, `A` and `B` and
  **no `C` and no `D`** — no command text, no exit codes, no durations. Suggestions still work
  (the grid read between `B` and the cursor does not need `C`); history capture falls back to the
  same heuristic an un-instrumented session uses.

## Third-party integrations

You do not have to use Nova's snippets. Anything that emits OSC 133 works — iTerm2's
`shell_integration`, VS Code's `shell-integration.sh`, `starship`'s, a hand-rolled one. Nova's
parser has never cared who wrote the marks.

Two things to know about the third-party ones:

- **A bare `133;C` is fine.** FinalTerm does not require a payload and several integrations send
  none. Nova treats a payload-less `C` as the lifecycle edge it is — the command-input window
  closes, the suggestion surface goes quiet, `D` still patches the exit code — and falls back to
  reading the command line off the grid at Enter for history. You lose nothing but the guarantee
  that a multi-line or edited command is recorded exactly.
- **A plain-text `133;C;git status` is also fine**, and is read as written. `133;C;aid=7` is not
  treated as a command: FinalTerm allows `key=value` attributes on these marks, and writing `aid=7`
  into your permanent history would be worse than recording nothing.

## What Nova does with the marks

Arming is unconditional for SSH panes: the OSC 133 translator is attached when the session starts,
before any mark has arrived, because `A` and the first `B` arrive with the very first remote prompt
and a translator armed after them would miss the mark that opens the first command-input window.
It is inert on a host with no snippet — every path into it is a mark callback — so an
un-instrumented SSH session behaves exactly as it did before.

The session is promoted to "integrated" the first time any mark arrives, and Command Assist's
context reaches the same conclusion independently from the event stream, so the promotion cannot be
lost to a race or undone by an unrelated directory change.

Arming does **not** consult `CommandAssistPowerShellIntegrationEnabled`. That setting exists to let
you keep Nova from *injecting* a bootstrap into a local pwsh, which is the operation people turn off
because it interacts badly with a hand-built profile. Over SSH there is no injection to decline, the
emitter is one the user installed themselves, and the pane has no way to know which shell is on the
far end at arm time anyway — so remote arming is governed by the general shell-integration switch
alone.

Turning **shell integration** off in Settings turns off remote consumption too, and that covers all
three consumers, not just the arming: the tracker is not armed, the "this session is integrated"
latch does not flip, and a `133;C` payload is not adopted as the pane's last relevant command. It is
the same switch that decides whether Nova injects locally, and a remote host is the one place where
you cannot simply uninstall the emitter. (Mark-based overlay *anchoring* is not gated on it — that
path reads the parser's mark directly and predates the switch having a remote meaning.)

## Threat model

**What an instrumented remote session lets the far end do.** OSC 133 marks are bytes on the pane's
output stream, and nothing about a byte stream says who wrote it. On an SSH session that means the
remote host — or anything running on it, or anything that can get output onto that stream — can
write a `133;C;<base64>` and have the text inside it become a **command history entry**, tagged
`ShellIntegration`, attributed to the pane's host and working directory. History is global and
cross-session, so that entry is then ranked and suggested in *other* sessions, including local ones.
A hostile or compromised host can therefore seed your history with text you never typed and see it
offered back to you later somewhere else.

This is inherent to the OSC 133 contract and not specific to Nova: iTerm2, VS Code's terminal and
WezTerm all consume the same marks from the same untrusted stream, and all of them will record what
a remote host asserts about the command it ran. Nova's snippets do not create the exposure; enabling
consumption of anyone's marks does. Note that there is a pre-existing **local** equivalent, which
Phase 2b does not change: any local integrated pane rendering untrusted output — a `cat` of a
crafted file, a build log, `curl | less` — is a stream that can carry a `133;C` just as well.

**What it does not let the far end do.**

- **Nothing is executed.** A history entry is text. Getting it to run requires the user to select
  the suggestion and press Enter; insertion is explicit and the inserted text is visible in the
  command line before it is submitted.
- **Suggestions are never auto-submitted**, and `Ctrl+Enter` insertion puts the text on the command
  line rather than running it.
- **Redaction still runs.** The secrets filter is applied to structured entries on the same path as
  heuristic ones, so a payload shaped like a credential is redacted before it is stored.
- **Filesystem path suggestions stay off** for every remote session (see above), so nothing the
  remote asserts can steer a local path completion.
- **Volume is bounded.** At most one structured entry is recorded per prompt cycle, and a `133;C`
  payload over 8 KiB is dropped before it is decoded. A host cannot flood history from a single
  prompt.

**The escape hatch.** Set `CommandAssistShellIntegrationEnabled` to `false` (Settings → Command
assistant → shell integration). With it off, remote marks are parsed but not consumed: no tracker,
no integrated-session promotion, no structured capture, no adoption of the `C` payload as the
session's last command. The cost is that local injection stops too — it is deliberately one switch,
because "participate in the OSC 133 contract" is one decision.

**Follow-up (not in this change): surface provenance at suggestion time.** A remote-sourced history
entry is visually identical to a locally typed one in the suggestion list today. The entry already
carries `IsRemote`, `HostId` and `Source`, so the data is there; what is missing is showing it, so a
user can tell "this came from `prod-web-3`" from "you typed this". Tracked as the natural next step
once there is a second reason to touch the suggestion row rendering.

## Troubleshooting

- **Nothing changed.** Marks only take effect on a *new* session; sourcing the snippet into a shell
  Nova is already attached to works, but the prompt has to repaint at least once for `B` to land.
- **Suggestions appear but history stays empty.** Check `CommandAssistHistoryEnabled`; capture is
  gated separately from the feature.
- **Duplicate prompt marks / doubled output.** Something re-sourced the snippet in a way that
  defeated the load guard. Open a fresh session; if it persists, the culprit is a prompt framework
  rebuilding the hook chain after us — file it with the framework name.
- **The overlay still sits in the lower band.** That is the markless fallback, so no mark is
  reaching Nova. Confirm the remote shell is the one you instrumented (`echo $0`) and that the
  session is interactive.
