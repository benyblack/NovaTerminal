# NovaTerminal SSH Roadmap

_Last reviewed: 2026-04-27._

NovaTerminal supports two SSH backends:

- **OpenSSH** (production) — drives `ssh` in a PTY with a
  NovaTerminal-generated config file.
- **Native SSH** (default for new profiles) — an in-process Rust SSH crate with
  a poll-based ABI, bypassing external `ssh` entirely.

---

## Native SSH status (current)

_Source date: 2026-04-08, updated through 2026-04-27._

The native SSH initiative is implemented behind conservative rollout controls.

### Completed native SSH capabilities

- Backend split between OpenSSH and native SSH
- Native Rust SSH crate with poll-based ABI
- Avalonia host-key and auth dialogs
- SSH agent authentication (SSH_AUTH_SOCK on Unix; the OpenSSH service pipe, then
  Pageant, on Windows) — agent identities are offered after a configured identity
  file (which keeps first position, so an agent full of unrelated keys cannot
  exhaust the server's auth tries before the key that used to connect the profile)
  and before anything that prompts; a profile explicitly pinned to an identity
  file skips the agent entirely. No agent, an empty agent, and refused keys all
  fall through, and every agent protocol operation is time-bounded (signing gets
  a longer allowance for hardware-token touch prompts), so a wedged agent reads
  as no agent rather than a hung session. Jump hops and SFTP transfers take the
  same path, including the file-then-agent fallback.
- App-managed native known-host trust store
- Native SFTP file and folder upload/download
- Local port forwarding
- Direct-host dynamic port forwarding (SOCKS5)
- Jump-host support, single hop or a chain of any length (nested direct-tcpip, as OpenSSH treats `-J`)
- Remote port forwarding (server-side `tcpip-forward` listeners, as OpenSSH treats `-R`)
- Rollout controls, backend selector, and stable failure classification
- Resize coalescing for fullscreen TUIs (vim, htop, tmux)
- Keepalive honoring user settings
- Disconnect state surfaced in the terminal pane
- Runtime (session-scoped) password memory, opt-in

### Rollout guidance

- `Native` is the default backend for NEW profiles, wherever the global native
  toggle (`TerminalSettings.ExperimentalNativeSshEnabled`, on by default,
  Settings > SSH) is enabled; with the toggle off, new profiles default to
  `OpenSsh`, so the default can never point at a backend that refuses to
  connect. Existing profiles keep their stored backend, and the model-level
  defaults stay `OpenSsh` on purpose: a profile store whose JSON predates the
  backend field must load as the backend it was actually using, never silently
  migrate.
- Native SSH does **not** silently fall back to OpenSSH on failure.
- Settings the native backend cannot honor are warned about, not silently
  dropped: a native profile with multiplexing (ControlMaster) options or extra
  SSH arguments — both of which drive the OpenSSH client and have no native
  equivalent — gets a notice in the profile editor and a warning line in the
  terminal at connect time. The settings stay stored, so switching the profile
  back to OpenSSH restores them intact.
- Whether native *can* serve a given profile is answered in one place,
  `NativeSshCapability.Evaluate`. Its one refusal today is a remote forward with
  source port 0 (a server-allocated listen port, OpenSSH's `-R 0:`): the native
  backend matches incoming connections back to their rule by the requested port,
  so a server-picked port has no rule to land on yet. Everything else — jump-hop
  chains, local/dynamic/remote forwards — is served natively. The gate's call
  sites (editor at save time, factory and session at connect time) all enforce
  the same verdict.
- Beyond that one shape, the only native refusal left is the global
  experimental toggle — reversible under Settings > SSH.
- Native SSH file transfers use the built-in native SFTP path.
- Native SSH transfer dialogs can autocomplete remote paths when an active session for that profile already exists.
- Native SSH single-file transfers show live byte progress when the total size is known; folder transfers report per-file progress only.
- OpenSSH file transfers still use the system `scp` path.
- Native SSH supports local and dynamic forwards, on direct connections and
  through jump hops alike — the forward channels ride the target session,
  however that session was reached.
- Native SSH supports jump chains of any length. Each hop is a full SSH session
  (its own host-key verification and authentication) nested over a direct-tcpip
  channel of the previous hop; SFTP transfers and remote browsing take the same
  chain.
- Native SSH supports remote forwards: a `tcpip-forward` listener is requested on
  the server per rule once the session is established (the Connected event, so no
  thread waits out the handshake), and incoming connections ride the same
  forward-channel machinery as the outgoing kinds. A request the server refuses
  is loud but not fatal, matching `ssh -R`: the warning is printed into the
  terminal and the session survives. Forwarded-tcpip channels the client never
  asked for are refused at the Rust handler, so an unsolicited open from a
  hostile server is never registered.

### Verification

- See [`docs/native-ssh/Native_SSH_Test_Matrix.md`](native-ssh/Native_SSH_Test_Matrix.md)
  for the automated verification set and the remaining manual checks.

### Open question: routing a profile that expressed no preference

`NativeSshCapability` answers "can native serve this profile?", which is what a
future default flip needs — but it is deliberately *not* wired to a routing
decision yet, and `SshBackendKind` still has only `OpenSsh` and `Native`.

Adding an `Auto` value looks like the obvious next step and is a trap in its
current form. Roughly eight sites decide native-only behaviour by comparing the
**persisted** backend kind to `SshBackendKind.Native` — the SFTP path
(`SftpService`), the remote file browser and path autocomplete
(`RemoteDirectoryBrowserService`), session-scoped password memory
(`ActiveSshSessionRegistry`), replay/agent-host descriptors, and the pane's own
checks. An `Auto` profile that resolved to native at connect time would silently
take the *OpenSSH* path in all of them, so file transfers and remote browsing
would quietly use `scp`/no-op paths against a native session. The backend combo in
the editor also binds straight to `Enum.GetValues<SshBackendKind>()`, so a new
value appears in the UI unlabelled.

So `Auto` needs a resolved-backend concept threaded through those call sites
first — a session-scoped answer rather than a profile field. That is its own
change, not a rider on the capability gate.

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
