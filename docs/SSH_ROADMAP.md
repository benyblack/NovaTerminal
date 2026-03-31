Milestone Implementation Roadmap 🗺️
====================================

Below milestones are designed to land value early and keep architecture clean.

M4.0 --- Foundations (Profiles + Session Type)
--------------------------------------------

**Outcome:** You can create a profile and connect via OpenSSH reliably.

-   `NovaTerminal.Core.Ssh` project/module

-   Domain models: `SshProfile`, `PortForward`, `SshJumpHop`, `SshMuxOptions`

-   `ISshProfileStore` with JSON persistence + schema version

-   `SshSession` implements `ITerminalSession` (spawns `ssh` in PTY)

-   Basic "New SSH Connection" dialog (minimal fields)

-   Smoke test: connect to a simple host

Deliverable: profiles saved, connect works.

* * * * *

M4.1 --- OpenSSH Config Compiler (Determinism + Debuggability)
------------------------------------------------------------

**Outcome:** Profile → compiled config → stable alias launch.

-   `IOpenSshConfigCompiler` (compile all profiles into `ssh_config.generated`)

-   Atomic writes + file locking strategy (avoid concurrent writes)

-   Alias convention: `nova_<profileId>`

-   Launch uses `ssh -F <generated> nova_<id>`

-   Diagnostics: show resolved ssh path + "copy launch command"

-   Unit tests: compiler golden tests

Deliverable: reproducible connections, easy bug reports.

* * * * *

M4.2 --- Termius-like Management UI (Search/Tags/Groups/Favorites)
----------------------------------------------------------------

**Outcome:** "Connection manager" feels real.

-   SSH Manager view (list + search)

-   Favorites, tags, group path

-   Open in: current pane / new tab / split H/V

-   Profile editor UI (Basic/Auth/Jump/Forwards)

-   Validation UI (bad host, missing identity file)

Deliverable: most of the "it feels premium" effect.

* * * * *

M4.3 --- Port Forward Presets + Jump Host Graph
---------------------------------------------

**Outcome:** Power-user workflows are fast.

-   Port forward editor table (add/clone/enable)

-   Forward presets ("Postgres", "Redis", "SOCKS proxy")

-   Jump host reorder + graph preview

-   Export/import profile (sanitized)

Deliverable: practical devops workflows.

* * * * *

M4.4 --- Multiplexing (ControlMaster) + Reliability Hardening
-----------------------------------------------------------

**Outcome:** Multi-pane SSH becomes *snappy*.

-   `SshMuxOptions` UI + config emission

-   ControlPath strategy:

    -   short path on Windows

    -   per-profile stable path

-   Keepalive defaults + UI

-   Failure classifier + friendly error surface

Deliverable: "feels like a real SSH client" without being one.

* * * * *

M4.5 --- Host Key UX Polish + Telemetry Hooks
-------------------------------------------

**Outcome:** Less scary prompts, better supportability.

-   Detect host key prompt output patterns and show a dialog

-   Known hosts isolation option (app-managed file)

-   "Diagnostics mode" per launch (`-v/-vv`) with redaction

-   Metrics:

    -   time to first output

    -   session duration

    -   exit code histogram

Deliverable: fewer support issues, higher trust.

* * * * *

M4.6 --- QA & Regression Suite
----------------------------

**Outcome:** You can ship this.

-   Integration tests (where feasible) using `ssh -G` config sanity checks

-   Manual test checklist & scripted setup

-   Docs: "SSH Profiles" user guide + troubleshooting

Deliverable: release-ready SSH management feature set.

* * * * *

Native SSH Status (2026-03-31)
==============================

The native SSH initiative is now implemented behind conservative rollout controls.

Completed native SSH capabilities:

- backend split between OpenSSH and native SSH
- native Rust SSH crate with poll-based ABI
- Avalonia host-key and auth dialogs for native SSH
- app-managed native known-host trust store
- local port forwarding for the native backend
- one-hop native jump-host support
- rollout controls, backend selector, and stable failure classification

Rollout guidance:

- `OpenSsh` remains the default backend.
- `Native` is gated by `TerminalSettings.ExperimentalNativeSshEnabled`.
- Native SSH does not silently fall back to OpenSSH on failure.
- Multi-hop jump chains remain unsupported in the native backend.

Verification references:

- See `docs/native-ssh/Native_SSH_Test_Matrix.md` for the automated verification set and the remaining manual checks.

* * * * *

Suggested "Pro" gating later 💸 (optional)
==========================================

Keep it simple early, but here are clean monetizable levers:

-   Advanced multiplex profiles (shared connection pools)

-   Team-shared profile packs (without full SaaS yet: file-based export/import)

-   Connection templates + compliance checks

-   Enhanced diagnostics bundles

