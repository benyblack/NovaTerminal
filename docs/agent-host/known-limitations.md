# Agent host — known limitations

Current limitations of the agent-host surface (A1–A4). None are correctness bugs
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
  render-to-text of the final screen; frame-stepping and image output are
  out of scope for now (Virtual fast-forward already covers most of it).
- **Replay ships in the app executable** — the self-contained AOT release bundle
  contains no separate `NovaTerminal.Cli`; the app exe serves `--replay <file>`
  itself (`NovaTerminal --replay …`), the same headless render as the dev CLI.
