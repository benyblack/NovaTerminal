# NovaTerminal SSH Roadmap

_Last reviewed: 2026-04-27._

NovaTerminal supports two SSH backends:

- **OpenSSH** (default, production) — drives `ssh` in a PTY with a
  NovaTerminal-generated config file.
- **Native SSH** (experimental, opt-in) — an in-process Rust SSH crate with a
  poll-based ABI, bypassing external `ssh` entirely.

---

## Native SSH status (current)

_Source date: 2026-04-08, updated through 2026-04-27._

The native SSH initiative is implemented behind conservative rollout controls.

### Completed native SSH capabilities

- Backend split between OpenSSH and native SSH
- Native Rust SSH crate with poll-based ABI
- Avalonia host-key and auth dialogs
- App-managed native known-host trust store
- Native SFTP file and folder upload/download
- Local port forwarding
- Direct-host dynamic port forwarding (SOCKS5)
- One-hop jump-host support
- Rollout controls, backend selector, and stable failure classification
- Resize coalescing for fullscreen TUIs (vim, htop, tmux)
- Keepalive honoring user settings
- Disconnect state surfaced in the terminal pane
- Runtime (session-scoped) password memory, opt-in

### Rollout guidance

- `OpenSsh` remains the default backend.
- `Native` is gated by `TerminalSettings.ExperimentalNativeSshEnabled`.
- Native SSH does **not** silently fall back to OpenSSH on failure.
- Native SSH file transfers use the built-in native SFTP path.
- OpenSSH file transfers still use the system `scp` path.
- Native SSH supports local forwards and direct-host dynamic forwards.
- Dynamic forwarding through a one-hop jump host is **not** yet supported.
- Remote forwarding is **not** supported in the native backend.
- Multi-hop jump chains are **not** supported in the native backend.

### Verification

- See [`docs/native-ssh/Native_SSH_Test_Matrix.md`](native-ssh/Native_SSH_Test_Matrix.md)
  for the automated verification set and the remaining manual checks.

---

## Suggested "Pro" gating (optional, future)

Kept deliberately simple early on. Clean monetizable levers if needed later:

- Advanced multiplex profiles (shared connection pools)
- Team-shared profile packs (file-based export/import, no SaaS)
- Connection templates + compliance checks
- Enhanced diagnostics bundles

---

## Historical milestones (OpenSSH backend)

_Preserved for context. All items below are shipped._

### M4.0 — Foundations (Profiles + Session Type)

**Outcome:** Create a profile and connect via OpenSSH reliably.

- `NovaTerminal.Core.Ssh` project/module
- Domain models: `SshProfile`, `PortForward`, `SshJumpHop`, `SshMuxOptions`
- `ISshProfileStore` with JSON persistence + schema version
- `SshSession` implements `ITerminalSession` (spawns `ssh` in PTY)
- Basic "New SSH Connection" dialog
- Smoke test: connect to a simple host

### M4.1 — OpenSSH Config Compiler

**Outcome:** Profile → compiled config → stable alias launch.

- `IOpenSshConfigCompiler` (compiles all profiles into `ssh_config.generated`)
- Atomic writes + file locking strategy
- Alias convention: `nova_<profileId>`
- Launch uses `ssh -F <generated> nova_<id>`
- Diagnostics: show resolved ssh path + "copy launch command"
- Unit tests: compiler golden tests

### M4.2 — Termius-like Management UI

**Outcome:** Connection manager feels real.

- SSH Manager view (list + search)
- Favorites, tags, group path
- Open in: current pane / new tab / split H/V
- Profile editor UI (Basic/Auth/Jump/Forwards)
- Validation UI (bad host, missing identity file)

### M4.3 — Port Forward Presets + Jump Host Graph

**Outcome:** Power-user workflows are fast.

- Port forward editor table (add/clone/enable)
- Forward presets ("Postgres", "Redis", "SOCKS proxy")
- Jump host reorder + graph preview
- Export/import profile (sanitized)

### M4.4 — Multiplexing (ControlMaster) + Reliability Hardening

**Outcome:** Multi-pane SSH feels snappy.

- `SshMuxOptions` UI + config emission
- ControlPath strategy (short path on Windows, per-profile stable path)
- Keepalive defaults + UI
- Failure classifier + friendly error surface

### M4.5 — Host Key UX Polish + Telemetry Hooks

**Outcome:** Less scary prompts, better supportability.

- Detect host key prompt output patterns and show a dialog
- Known hosts isolation option (app-managed file)
- Diagnostics mode per launch (`-v`/`-vv`) with redaction
- Metrics: time to first output, session duration, exit code histogram

### M4.6 — QA & Regression Suite

**Outcome:** Release-ready SSH feature set.

- Integration tests (`ssh -G` config sanity checks)
- Manual test checklist & scripted setup
- User-facing SSH profiles guide + troubleshooting
