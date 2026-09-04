# Agent host — known limitations

Current limitations of the agent-host surface (A1–A5). None are correctness bugs
in the deterministic core; they are boundaries of what the current signals can
observe. Tracked against the DIRECTION follow-ups.

## Heuristic session status can't see inside WSL or remote SSH

`novaterminal.get_session_status` (and the status column in `list_sessions`) has
two confidence tiers:

- **precise** — driven by shell-integration events (prompt / command lifecycle).
  Accurate regardless of where the process runs.
- **heuristic** — inferred from PTY signals, primarily "does the shell have an
  active child process?" via the OS process tree.

The heuristic child-process probe only sees processes in the **host OS** process
tree. It therefore **cannot see**:

- processes running inside a **WSL** distribution (they live in the WSL VM's
  Linux namespace, not as Windows children of the launched `wsl.exe`), or
- processes running on a **remote SSH** host.

Consequence: a genuinely-running command in a WSL or SSH session may report
`awaitingInput` / `idle` at **heuristic** confidence, even though it is busy.
The reported tier always says `heuristic` in that case, which is the signal to
treat running/idle as a guess.

**Accurate today:**
- Native local shells (`cmd`, PowerShell) — a running command is a real host-OS
  child process, so status is correct even at the heuristic tier.
- Any session with **shell integration enabled** — it reports at the **precise**
  tier from prompt/command events, which is accurate for WSL and SSH too.

**One exception to the precise tier:** the PowerShell bootstrap emits `OSC 133;C`
(command accepted) and `OSC 133;D` (command finished) from a wrapped PSReadLine
`Enter` handler, and skips that wrapping when PSReadLine is absent (minimal hosts,
server-core, some constrained-language modes). Such a session still reaches the
**precise** tier from the prompt marks (`OSC 133;A`/`B`) it does emit, but it never
reports `running` — with no `C`/`D` it looks permanently at a prompt, and it is not
covered by the child-process heuristic either, because the precise tier suppresses
it. Installing PSReadLine restores full status.

**Workarounds:**
- Enable shell integration in the shell (including inside WSL / on the remote)
  to get precise status.
- Use `wait_for_events` / `read_screen` to corroborate rather than relying on the
  heuristic running/idle alone.

**Planned fix:** foreground-process reporting for the heuristic tier
(DIRECTION A2 follow-up) — query the foreground process inside the WSL distro /
over the SSH channel so the heuristic tier is accurate there too.

## Other parked follow-ups

From `docs/agent-host/DIRECTION.md` and the milestone design docs:

- **OS-native completion notifications** — A2 currently shows an in-app toast
  only; native OS notification backends are a follow-up.
- **`Idle` transitions surface only via the 1 s sweep** — an idle transition is
  observed on the next sweep tick, not instantaneously.
- **Replay `--replay --at <ms>` / PNG output** — A4 ships headless
  render-to-text of the final screen; frame-stepping and rendering a *replay
  file* to an image are out of scope for now (Virtual fast-forward already
  covers most of it). Rendering a *live* pane to a PNG shipped in A5 as
  `capture_screen`, and a replay-to-PNG path would reuse the same
  `TerminalSnapshotRenderer`.
- **Screenshots are the pane, not the window** — neither `capture_screen` mode
  photographs app chrome: `render` draws the terminal grid offscreen from the
  buffer, and `live` photographs the pane's own control. So tab bars, the
  command-assist bar and split borders are in neither. `live` does carry what is
  drawn *within* the pane that the buffer cannot describe — the user's background
  image and window opacity — at the cost of needing the pane on screen and of not
  being reproducible. A whole-window screenshot would still be a separate feature.
- **Screenshots need the pane to have been measured** — a pane that has never
  been laid out (created but never shown) has no cell geometry to render into and
  returns `captureUnavailable`; `read_screen` works regardless.
- **Replay ships in the app executable** — the self-contained AOT release bundle
  contains no separate `NovaTerminal.Cli`; the app exe serves `--replay <file>`
  itself (`NovaTerminal --replay …`), the same headless render as the dev CLI.
