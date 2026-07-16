<!--
DRAFT — publish alongside the release that actually ships the agent-host surface.

Before publishing, verify:
  1. A release (0.4.0) is cut whose binaries contain milestones A1–A4. The
     features described here are on `main` but were NOT in v0.3.0.
  2. `winget install benyblack.NovaTerminal` works (first-time winget-pkgs
     submission merged — see packaging/winget/submit-first-time.ps1). If not,
     drop the winget line from "Install" and keep the direct download.
  3. Update every version string / URL below to the released tag.
  4. Record the short demo referenced in "See it" (Claude Code reading a live
     session + waiting on a build), or remove that callout.

Targets: GitHub release notes (long form), a blog post, and a shortened
Show HN / Reddit r/commandline post. Keep the security framing first.
-->

# NovaTerminal: the terminal your AI agent can see

CLI coding agents — Claude Code, Codex, Gemini CLI — now live in the terminal.
But the terminal itself has stayed a black box to them: an agent kicks off a
build or a migration and then squints at scraped text, guessing whether it's
done, stuck, or waiting for input. NovaTerminal closes that gap. It's a fast,
correct, cross-platform terminal that also exposes a **structured, permissioned
interface to the agent running inside it** — over the Model Context Protocol,
so any MCP-capable agent can use it today.

The bet is simple: don't embed a chatbot in the terminal. Make the terminal the
best *host* for the agents people already use.

## Safety first, because this can act

The agent surface is **off by default and opt-in in two independent steps**:

- **Observe** (read the screen, scrollback, and live status) is one toggle.
- **Act** (type into, open, and close sessions) is a *separate* toggle on top of
  observe — turning on observe never grants acting.
- Driving a **remote SSH** session requires allowlisting that connection
  specifically; the check fails closed.
- Every action an agent takes — and every one it's denied — is recorded in a
  visible in-app **activity journal**. Nothing happens silently.

There's a written [threat model](../agent-host/2026-07-12-acting-threat-model.md)
covering the trust boundaries, the controls, and the risks we explicitly accept.
The endpoint is local and per-user; there is no network-exposed control surface.
When everything is off, the agent interface doesn't exist.

## What an agent can do

**Observe** — read exactly what a human sees. Screen and scrollback come from
the same deterministic snapshot the terminal renders from, so the agent isn't
parsing a lossy scrape; it's reading the real cell grid, cursor, and styling.

**Know the state** — ask whether a session is running, waiting for input, idle,
or exited (with exit code), and long-poll for events: command finished, stalled,
bell. An agent can start a build and *wait* for it instead of polling `ps`.

**Act, with permission** — type input (byte-faithful, including control keys),
open a new tab from a named profile, or close a pane. Injected input flows
through the same path as human keystrokes, so it records and replays identically.

**Replay — debug what your agent did, frame by frame.** This is the part no
other terminal does. NovaTerminal keeps a bounded flight recording of recent
session output; an agent (or you) can export it as a deterministic `.rec` file
and re-render it headlessly:

```
NovaTerminal.Cli --replay session.rec
```

Byte-for-byte reproducible on Windows, macOS, and Linux — a real postmortem for
an agent run, not a scrollback screenshot. (Exports contain output only, never
anything typed.)

## Why this terminal

The agent features are only trustworthy because of what's underneath:

- **A cell-based VT engine with strong VT/ANSI conformance** — the structured
  read is accurate because the buffer is accurate.
- **Deterministic recording and replay** — the same core that powers agent
  replay has gated the project's correctness from day one.
- **Native SSH** — profiles, jump hosts, port forwarding — so agents can be
  handed correct remote sessions, not a fragile `ssh` wrapper.
- **Cross-platform, self-contained, AOT-compiled** — one download, no runtime.

## Install

```
# Windows
winget install benyblack.NovaTerminal

# Or download a self-contained build for your platform:
# https://github.com/benyblack/NovaTerminal/releases/latest
```

Point your agent at it by adding the MCP server (stdio). With Claude Code:

```
claude mcp add novaterminal -- <path-to>/NovaTerminal.McpServer
```

Then enable **Settings → Agent access (observe)** — and, only if you want the
agent to act, **Agent access (act)** — and watch the activity journal.

## See it

<!-- Embed the demo GIF/video: Claude Code reading a live vim/htop session, then
     waiting on a build via wait_for_events, then replaying the run. -->

## Where it's going

NovaTerminal is a nights-and-weekends project with a clear thesis. Next up:
broader distribution (Flathub, AUR, Homebrew), OS-native completion
notifications, and richer replay (frame stepping, image output). Issues and
ideas welcome at https://github.com/benyblack/NovaTerminal.

It's the correct, deterministic terminal — and now, the one your agent can
actually see.
