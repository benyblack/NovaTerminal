# Agent Host Acting Surface — Threat Model (A3)

_Status: current as of milestone A3 (2026-07-12). Scope: the permissioned
acting surface — `sendInput`, `spawnSession`, `closeSession` — added on top of
the observe (A1/A2) and replay-export (A4) surfaces. Read alongside
`docs/agent-host/DIRECTION.md` and `docs/plans/2026-07-12-agent-host-a3-act-design.md`._

## What the acting surface exposes

With permission, a local AI agent (via the stdio MCP server, proxied to the
running app over the per-user local IPC endpoint) can:

- **`sendInput`** — inject arbitrary bytes into a live session, exactly as if
  typed at the keyboard.
- **`spawnSession`** — open a new tab running a local profile, or an
  allowlisted SSH profile, by name.
- **`closeSession`** — close a live pane.

This is a meaningful escalation from observe: the agent can now run commands as
the user, on local shells and (when allowlisted) on remote hosts the user has
credentials for.

## Trust boundaries

```
AI agent ──stdio──▶ NovaTerminal.McpServer ──per-user local IPC──▶ NovaTerminal.App
 (untrusted intent)   (thin proxy, no policy)                      (all policy + gating here)
```

- The **MCP server is not a policy boundary** — it forwards calls. All gating
  is enforced in the App endpoint (`AgentHostService`).
- The **IPC endpoint is loopback/per-user only**: a Windows named pipe with
  `PipeOptions.CurrentUserOnly`, or a unix domain socket at mode 0600. It is
  never network-exposed (an unchanged DIRECTION non-goal). The threat actor is
  therefore a process already running as the user, or the agent the user has
  themselves connected.
- The user is trusted; the agent's *intent* is not. The design assumes the
  agent may be confused, jailbroken, or prompt-injected by hostile terminal
  content it read via observe.

## Controls

1. **Two-key gating, fail-closed.** Nothing acts unless (a) the observe
   endpoint is running (`AgentAccessObserveEnabled`) **and** (b) the separate
   `AgentAccessActEnabled` opt-in is on. Both default off; acting never rides
   the observe toggle. Denied → `actDisabled`.
2. **Per-profile SSH allowlist.** Acting on an SSH session (input, or spawning
   an SSH profile) additionally requires that profile's `AllowAgentAccess`
   flag (default off). The probe **fails closed**: if it is unavailable, or
   the profile is unknown, SSH acting is denied (`profileNotAllowed`). Local
   sessions are governed by the act toggle alone — a local shell is already
   reachable by any process running as the user, so a local allowlist would be
   security theater; remote sessions reach *other* machines with the user's
   credentials and so are gated per profile.
3. **Visible activity journal.** Every acting attempt — allowed **and denied**,
   including malformed requests — is recorded to an in-app activity journal
   ("nothing is silent", mirroring the credential-consent principle). The
   journal is the consent/visibility surface, especially for `closeSession`,
   which bypasses the interactive close-confirmation dialog (an agent cannot
   answer a modal).
4. **Input size cap.** `sendInput` is capped at 32 KiB per call to bound a
   flood; the endpoint is per-user local so this is a robustness bound, not a
   defense against a remote attacker.
5. **Instant revocation.** Turning the act toggle off is read on the next
   request (volatile flag); turning observe off tears the endpoint down and
   force-closes live connections. Closing the window clears the UI executor and
   allowlist probe.

## Residual risks (accepted)

- **A connected agent acting on hostile content.** If the user enables acting
  and an agent is prompt-injected by terminal output, it can run commands as
  the user within the granted scope. Mitigation is the default-off gating, the
  per-profile SSH allowlist, and the visible journal — not prevention. Users
  should enable acting only for agents they trust, and allowlist only the SSH
  profiles they intend agents to drive.
- **`closeSession` bypasses the confirm dialog.** Deliberate: closing is
  bounded (ends a visible session, cannot exfiltrate or execute) and the act
  opt-in plus journal entry are the consent surface. A confused agent could
  close a pane with unsaved in-terminal state; this is the same risk as a
  mis-click and is journaled.
- **The journal is in-memory and bounded (200 entries).** It is a *visibility*
  surface, not a tamper-evident audit log — the agent's own MCP transcript is
  the authoritative record. A high-volume attacker could roll older entries
  out of the ring.
- **Byte-faithful input includes control characters.** By design (Ctrl-C,
  Enter, etc. must work). The cap and gating bound abuse; there is no content
  filtering of injected input (it would be trivially bypassable and break
  legitimate use).

## Explicit non-goals

- No network-exposed control endpoint.
- No per-agent identity, authentication, or rate limiting beyond the size cap
  (the endpoint is single-user local).
- No sandboxing of what a spawned/driven shell can do — it runs with the
  user's own privileges, exactly as a terminal the user opened.
- Not a defense against a local process already running as the user (such a
  process can drive the terminal directly regardless).
