# NovaTerminal

<img width="100" alt="ico" src="https://github.com/user-attachments/assets/cffc2a9b-4c2b-4ee9-b03d-1d4c3c793d85" />

**NovaTerminal** is a modern, cross-platform terminal emulator focused on

**correctness, performance, and predictability**.


Built with:

- **.NET 10**
- **Avalonia UI**
- **Skia (GPU-accelerated rendering)**
- **Rust-based PTY backend**

Supported platforms: **Windows · Linux · macOS**

<img width="250" alt="3" src="https://github.com/user-attachments/assets/f871a580-9857-4546-98d0-0356b3176dc4" />
<img width="250" alt="2" src="https://github.com/user-attachments/assets/82d9d37f-5370-446f-9a94-1e58a3665986" />
<img width="250" alt="1" src="https://github.com/user-attachments/assets/a0c52576-73e5-4bce-a6df-69877b53032f" />
<img width="250" alt="5" src="https://github.com/user-attachments/assets/ea279510-57af-4af5-9f84-ebff05a34364" />
<img width="250" alt="4" src="https://github.com/user-attachments/assets/9354d0fb-54ff-4080-a81c-5b49fded6d53" />

---

### ✨ Why NovaTerminal?

Most terminal emulators optimize for speed or features. NovaTerminal focuses on something different:

-   🧪 **Deterministic rendering**\
    Same input → same output. Always. Enables reliable testing and replay.
-   📼 **Replay-driven debugging**\
    Record terminal sessions and replay them with pixel-level consistency.
-   ✅ **VT correctness first**\
    Built with conformance and standards in mind---not best-effort rendering.
-   ⚡ **GPU-accelerated rendering**\
    Smooth, modern rendering pipeline using Skia.
-   🧩 **Extensible architecture**\
    Designed for future workflows (cloud, automation, AI-assisted tooling).
-   🤖 **Built for AI agents**\
    An opt-in MCP server lets Claude Code and other agents observe your live terminal sessions --- and, behind a separate opt-in, drive them.

> **Terminal correctness is enforced by automated tests, not guesswork.**

That principle shows up everywhere: VT behavior is measured against a
conformance matrix, the renderer is gated by performance contracts, and
replay parity prevents silent behavioral drift.

---

## Install

GitHub release assets are produced as Native AOT bundles for `win-x64`,
`linux-x64`, and `osx-arm64`. Every release runs the gating unit-test lane on
all three OSes before any bundle is published.

**Windows**

- **Installer** — download `NovaTerminal-Setup-win-x64-<tag>.exe` from the
  [latest release](https://github.com/benyblack/NovaTerminal/releases/latest). Installs per-user
  (no admin prompt), adds Start Menu and Desktop shortcuts, and checks for updates in the
  background — a new version downloads quietly and is applied when you accept the prompt and
  restart. Never a surprise restart. Automatic checks can be turned off in Settings.
- **Portable** — download `NovaTerminal-win-x64-<tag>.zip` and extract it anywhere. No updater.
- **winget** — `winget install benyblack.NovaTerminal` (portable package).

The installer and the executables are **not code-signed yet** ([#91](https://github.com/benyblack/NovaTerminal/issues/91)),
so SmartScreen will warn on first run. Choose *More info → Run anyway*.

**macOS**

- **macOS (Apple Silicon)** — download `NovaTerminal-Setup-osx-arm64-<tag>.pkg` from the
  [latest release](https://github.com/benyblack/NovaTerminal/releases/latest) and run it;
  it installs into `/Applications` (or `~/Applications`) as a proper `NovaTerminal.app`
  bundle. Alternatively grab `NovaTerminal-osx-arm64-<tag>.zip`, open it, and drag
  `NovaTerminal.app` to `/Applications`.

macOS builds installed via the `.pkg` check for updates in the background and apply them
on restart, same as Windows. If the app lives in `/Applications`, macOS will ask for your
password once per update.

Releases built while the macOS signing secrets are unset (see
[packaging/macos](packaging/macos/README.md), tracking
[#91](https://github.com/benyblack/NovaTerminal/issues/91)) are **not code-signed or
notarized**, so Gatekeeper blocks their first launch. On macOS 13+:

1. Try to open `NovaTerminal` once (it will be blocked — that's expected).
2. Open **System Settings → Privacy & Security**, scroll down, and click **Open Anyway**.
3. Confirm — macOS remembers the approval for subsequent launches and updates.

From a terminal, the one-liner equivalent is
`xattr -cr /Applications/NovaTerminal.app`.

**Linux**

Requires **glibc 2.35 or newer** — Ubuntu 22.04+, Debian 12+, Fedora 36+, or a current
rolling distro. Debian 11 and RHEL 8/9 are not supported.

**AppImage** (recommended — updates itself):

```sh
# Replace <tag> with the latest release, and x64 with arm64 on ARM machines.
curl -LO https://github.com/benyblack/NovaTerminal/releases/download/<tag>/NovaTerminal-linux-x64-<tag>.AppImage
chmod +x NovaTerminal-linux-x64-<tag>.AppImage
mkdir -p ~/Applications && mv NovaTerminal-linux-x64-<tag>.AppImage ~/Applications/
~/Applications/NovaTerminal-linux-x64-<tag>.AppImage
```

Keep it somewhere you can write, such as `~/Applications` — the app updates itself by
rewriting the AppImage, which it cannot do from a root-owned path like `/opt`.

Ubuntu 22.04 and later ship no FUSE 2 by default, which AppImages need. Either
install it (`sudo apt install fuse`, which pulls in `libfuse2` — `libfuse2` alone
supplies only the library, not the `fusermount` binary the mount step needs) or
run with `--appimage-extract-and-run`.

**Debian / Ubuntu package** (system integration; update via your package manager):

The `.deb` filename is not the release tag substituted into a template, unlike
every other asset on this page. `build-deb.sh` always appends a Debian revision
(`-1`), and for a prerelease tag it also turns the `-` before the prerelease
label into `~` — dpkg reads the *last* `-` in a version as the revision
separator, so a prerelease left as `-beta.1` would parse as upstream `0.5.0`
revision `beta.1` and sort *above* the eventual `0.5.0-1` final release; `~`
sorts before everything, so `~beta.1-1` correctly sorts below it. `v0.5.3` ships
as `novaterminal_0.5.3-1_amd64.deb`; `v0.5.0-beta.1` is built as
`novaterminal_0.5.0~beta.1-1_amd64.deb`, but GitHub replaces `~` with `.` in
release asset names, so the file you'll actually see on the releases page for a
prerelease is named `novaterminal_0.5.0.beta.1-1_amd64.deb` — the package's
internal `Version:` field (what dpkg reads) still carries the `~`, so
installation works either way; stable tags have no `~` to sanitize, so this
doesn't affect them. That's exactly why you shouldn't construct the filename
yourself — copy it verbatim from the [releases page](https://github.com/benyblack/NovaTerminal/releases):

```sh
curl -LO https://github.com/benyblack/NovaTerminal/releases/download/<tag>/<exact .deb filename from the release page>
sudo apt install ./<same filename>
nova
```

On ARM machines, grab the `arm64` asset instead of `amd64` — the `.deb` uses
Debian architecture names (`amd64`/`arm64`), not the `x64`/`arm64` RID names the
AppImage and tarball below use.

Installs `nova` on your PATH, an app-menu entry, and `man nova`. The in-app updater is
inactive for package installs by design.

**Portable tarball** (no integration):

```sh
# Replace <tag> with the latest release, and x64 with arm64 on ARM machines.
curl -LO https://github.com/benyblack/NovaTerminal/releases/download/<tag>/NovaTerminal-linux-x64-<tag>.tar.gz
tar -xzf NovaTerminal-linux-x64-<tag>.tar.gz && ./NovaTerminal
```

NovaTerminal is not registered as your default terminal. To do that yourself after
installing the `.deb`, see `man nova`.

For details on what each Linux package contains and its known limitations, see
[packaging/linux](packaging/linux/README.md).

For build steps, jump to [Build & test](#build--test) below.

---

## Features

### Terminal core

- VT / ANSI parsing measured against a conformance matrix
- Alternate screen support (`vim`, `less`, `htop`)
- Scrollback buffer
- Stable resize & reflow
- Cell-based buffer model
- Thread-safe, crash-resistant PTY backend

### UI

- Tabs and split panes, reorderable by drag or by `Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown`
- Vertical tab sidebar (`Ctrl+Shift+L`) with per-tab status, agent-activity chips, and a live output preview
- Agent Output panel — renders a command's output as markdown beside the pane, streaming as it arrives
- Command palette
- Search overlay
- Profiles (local & SSH)
- Bundled themes (Dracula, Nord, Gruvbox, Tokyo Night, Catppuccin Mocha, Solarized, GitHub, Monokai, OneHalf, Cobalt2), custom themes, and font configuration
- Live settings (no restart)

### Command Assist

Suggestions anchored to what the shell actually reports, rather than to a guess
about where the prompt ends — built on OSC 133 shell-integration marks.

- Reads the live command line from the terminal grid, not a shadow copy
- History search, snippet management, and a tldr-derived command catalogue
- Fix mode proposes a correction from a failed command's own output
- Passive suggestion bubble with rebindable shortcuts
- One-line shell-integration installer for remote hosts, with mark detection over SSH
- Still captures commands in sessions that emit no marks at all

### Configuration backup & restore

- Export settings, themes, connections, workspaces, policy and snippets to a
  single portable `.novabackup` file, and import it on another machine
- Six independently selectable categories; import merges or replaces
- Automatic background snapshots, deduplicated by content hash and retention-capped
- A snapshot is taken before every import and restore, so both are reversible
- Available from **Settings → Backup & Restore**, the command palette, and the CLI
  (`backup export|import|list|restore`); agents get read-only export and list MCP tools

> Connection passwords are **not** included in a bundle. They live in the OS
> credential store, not in the config folder, so imported SSH profiles need their
> passwords re-entered on first connect. See
> [`docs/CONFIG_STORAGE_CONTRACT.md`](docs/CONFIG_STORAGE_CONTRACT.md).

### Graphics & inline images

- **Sixel Graphics** (verified with `libsixel`, `lsix`, `gnuplot`)
- **iTerm2 Inline Images** (verified with `imgcat`, `test_iterm2.py`)
- **Kitty Graphics Protocol** (native on Linux/macOS; tunneled mode on Windows)
- **Proper ConPTY synchronization** — images render inline with prompts

### Native SSH

The in-process SSH client is the **default backend for new profiles**; OpenSSH
remains selectable per profile, and stays the default for profiles that predate
the flip.

- SSH profiles with platform-vault credential storage (Windows Credential Manager, macOS Keychain, Linux Secret Service)
- Password, identity-file, and ssh-agent authentication
- Multi-hop jump chains of any length
- Local, remote, and dynamic port forwarding
- Keepalive
- Coalesced resize handling for fullscreen TUIs (vim, htop, tmux)
- Disconnect state surfaced in the terminal pane
- Runtime password memory (opt-in, session-scoped)
- Native SFTP transfers and a pane-local remote-files sidebar

### Cross-platform parity
NovaTerminal guarantees identical terminal behavior across operating systems
for VT interpretation, buffer state, wrapping & reflow, and search semantics.
Platform-specific differences are limited to window chrome, blur/transparency,
global hotkeys, and credential storage backends.

### Agent access (MCP)

A local, stdio [Model Context Protocol](https://modelcontextprotocol.io) server
(`NovaTerminal.McpServer`) exposes NovaTerminal to AI coding agents (Claude Code, Claude
Desktop, VS Code, …):

- **Repo / dev-companion tools** — read-only and offline: project docs, VT/ANSI conformance
  data, and theme / SSH-profile / settings JSON validators.
- **Observe** (opt-in, default off) — `list_sessions`, `read_screen`, `read_scrollback`,
  `get_session_status`, `wait_for_events`, `export_replay`, `capture_screen`: read live sessions
  deterministically, as text or as a rendered PNG (screenshots need their own sub-toggle and are
  journaled).
- **Act** (a *separate* opt-in, on top of observe) — `send_input`, `spawn_session`,
  `close_session`: type into, open, and close sessions. SSH targets additionally require a
  per-profile allowlist, and every acting call — allowed or denied — is recorded in an in-app
  activity journal.

With both toggles off there is no live endpoint at all. See the
[MCP server README](src/NovaTerminal.McpServer/) and the
[acting threat model](docs/agent-host/2026-07-12-acting-threat-model.md).

---

## Use with AI agents (MCP)

Build the server, then register it with your MCP client, pointing at the **built DLL** (launch
the compiled DLL — never `dotnet run`, which corrupts the stdio stream):

```bash
scripts/build.ps1 build -c Release src/NovaTerminal.McpServer   # or scripts/build.sh
```

**Claude Code:**

```bash
claude mcp add novaterminal -- dotnet "<path-to-repo>/src/NovaTerminal.McpServer/bin/Release/net10.0/NovaTerminal.McpServer.dll"
```

For **Claude Desktop / VS Code**, add the same `command`/`args` to the client's MCP config.

The repo / dev-companion tools work immediately. To expose live sessions, enable
**Settings → Agent access (observe)** in NovaTerminal; to let an agent type into, spawn, or
close sessions, also enable the **Agent access (act)** sub-toggle (and allowlist any SSH
profiles you want reachable). Both are off by default.

---

## User documentation

- [User manual](docs/USER_MANUAL.md)
- [Tabs user manual](docs/TABS_USER_MANUAL.md)
- [Image protocol support](docs/IMAGE_PROTOCOL_SUPPORT.md)
- [SSH roadmap](docs/SSH_ROADMAP.md)

---

## For contributors & developers

### Architecture

NovaTerminal is organized into focused class libraries under `src/` with an
acyclic dependency graph.

- **[`src/NovaTerminal.App`](src/NovaTerminal.App/)** — Avalonia/UI layer: windows, themes, settings, orchestration.
- **[`src/NovaTerminal.Platform`](src/NovaTerminal.Platform/)** — Shared runtime primitives: input, paths, process, SSH.
- **[`src/NovaTerminal.VT`](src/NovaTerminal.VT/)** — Virtual Terminal engine: frame-agnostic parser logic and buffer state.
- **[`src/NovaTerminal.Rendering`](src/NovaTerminal.Rendering/)** — SkiaSharp rendering: framework-agnostic text shaping and GPU glyph caching.
- **[`src/NovaTerminal.Pty`](src/NovaTerminal.Pty/)** — Native OS integration and PTY session management.
- **[`src/NovaTerminal.Replay`](src/NovaTerminal.Replay/)** — Deterministic session recording and playback.
- **[`src/NovaTerminal.CommandAssist`](src/NovaTerminal.CommandAssist/)** — Command Assist domain, ranking, history/snippet storage, and shell integration. Avalonia-free by design; the App owns only its views.
- **[`src/NovaTerminal.Backup`](src/NovaTerminal.Backup/)** — `.novabackup` export/import, automatic snapshots, and the category-to-path catalogue. A leaf, so the MCP server can reference it without reaching into the app.
- **[`src/NovaTerminal.VtContract`](src/NovaTerminal.VtContract/)** — the machine-readable VT capability catalogue (`vt-capabilities.json`) and its schema validation, shared by the conformance tool, the parser tests, and the MCP dev tools.
- **[`src/NovaTerminal.Conformance`](src/NovaTerminal.Conformance/)** — VT conformance matrix tooling and report generation.
- **[`src/NovaTerminal.Cli`](src/NovaTerminal.Cli/)** — console-subsystem twin of the (WinExe) app for headless tooling: `vt-report`, headless replay (`--replay <file>`), and the SSH askpass helper.
- **[`src/NovaTerminal.AgentHost.Contracts`](src/NovaTerminal.AgentHost.Contracts/)** — zero-dependency wire contracts for the agent-host observe channel (shared by App and McpServer).
- **[`src/NovaTerminal.McpServer`](src/NovaTerminal.McpServer/)** — stdio-only MCP server exposing project docs, config validators, VT conformance data, and (opt-in) live terminal sessions to AI tooling: observe by default, and — behind a separate explicit opt-in — act (type into / open / close sessions).

Validation:

- **[`tests/NovaTerminal.App.Tests`](tests/NovaTerminal.App.Tests/)** — primary unit and integration suite (Avalonia Headless UI), including replay, render-metrics, golden-PNG, and shell-integration lanes.
- **[`tests/NovaTerminal.VT.Tests`](tests/NovaTerminal.VT.Tests/)**, **[`tests/NovaTerminal.Rendering.Tests`](tests/NovaTerminal.Rendering.Tests/)**, **[`tests/NovaTerminal.Platform.Tests`](tests/NovaTerminal.Platform.Tests/)**, **[`tests/NovaTerminal.McpServer.Tests`](tests/NovaTerminal.McpServer.Tests/)** — deterministic per-module suites (the blocking CI lane).
- **[`tests/NovaTerminal.Architecture.Tests`](tests/NovaTerminal.Architecture.Tests/)** — the key invariants of the graph below are *enforced*, not aspirational: NetArchTest checks at IL, csproj, and namespace level.
- **[`tests/NovaTerminal.Benchmarks`](tests/NovaTerminal.Benchmarks/)** — performance benchmarks and the SharpFuzz/libFuzzer harness.
- **[`tests/NovaTerminal.ExternalSuites`](tests/NovaTerminal.ExternalSuites/)** — manual vttest / native-SSH scenario driver.

```mermaid
graph TD
    Cli[NovaTerminal.Cli] --> App[NovaTerminal.App]
    App --> Platform[NovaTerminal.Platform]
    App --> VT[NovaTerminal.VT]
    App --> Rendering[NovaTerminal.Rendering]
    App --> Pty[NovaTerminal.Pty]
    App --> Replay[NovaTerminal.Replay]
    App --> CommandAssist[NovaTerminal.CommandAssist]
    App --> Backup[NovaTerminal.Backup]
    App --> Contracts[NovaTerminal.AgentHost.Contracts]
    Platform --> Pty
    Pty --> Replay
    Rendering --> VT
    Replay --> VT
    McpServer[NovaTerminal.McpServer] --> Contracts
    McpServer --> Backup
    McpServer --> VtContract[NovaTerminal.VtContract]
    Conformance[NovaTerminal.Conformance] --> VtContract
```

`CommandAssist`, `Backup`, `VtContract` and `AgentHost.Contracts` are leaves with
zero project references, and that is what lets `McpServer` share code with the
app without acquiring a path into `App`, `VT`, `Pty` or `Rendering`.

Enforced invariants (`NovaTerminal.Architecture.Tests`): `VT` is a leaf with zero project references; `Pty` must **not** depend on `VT` (the PTY layer delivers raw bytes only); `Replay` and `Rendering` reference exactly `VT`; no production assembly references test libraries. The remaining edges above are documented from the csproj references but not individually asserted.

---

### Engineering programs

#### Active work

- **Agent host program** — the accepted strategic direction
  ([`docs/agent-host/DIRECTION.md`](docs/agent-host/DIRECTION.md)): a
  session-facing MCP surface so AI agents can observe, query status of, and
  — with explicit, separate permission — act inside live terminal sessions
  (`send_input` / `spawn_session` / `close_session`, gated by an "Agent access
  (act)" opt-in on top of observe, a per-profile SSH allowlist, and a visible
  activity journal; threat model in
  [`docs/agent-host/2026-07-12-acting-threat-model.md`](docs/agent-host/2026-07-12-acting-threat-model.md)),
  with deterministic replay as the debugging story. Debug what your agent did,
  frame by frame: with both opt-in toggles enabled, an agent can call
  `novaterminal.export_replay` to save a session's recent output (never
  input — typed keys are not retained) as a standard `.rec` file, and anyone
  can re-render it deterministically with
  `NovaTerminal.Cli --replay <file> [--attributes]`. When the pixels are what
  matter — inline images, TUI layout, a rendering bug — `novaterminal.capture_screen`
  (its own "Agent screenshots" opt-in, also journaled) renders a pane to a PNG
  offscreen from its buffer, so a minimized or occluded window captures
  identically and nothing outside the pane can appear in the image.
- **VT conformance program** — every supported VT/ANSI feature is tracked in a
  matrix; a dedicated CI lane regenerates the report and fails on regressions.
  See [`docs/vt_coverage_matrix.md`](docs/vt_coverage_matrix.md) and
  [`docs/ghostty-gaps/vt_conformance_tooling.md`](docs/ghostty-gaps/vt_conformance_tooling.md).
- **Ghostty gap tracking (regression gate)** — comparison against Ghostty's
  behavior is maintained as a regression gate; remaining matrix gaps are
  closed when real TUI or agent workflows hit them. See
  [`docs/ghostty-gaps/`](docs/ghostty-gaps/) and
  [`docs/vt_ghostty_gap_matrix.md`](docs/vt_ghostty_gap_matrix.md).
- **Native SSH** — an in-process, cross-platform SSH client, now the default
  backend for new profiles, with VT correctness, resize coalescing, multi-hop
  jump chains, local/remote/dynamic forwarding, ssh-agent authentication,
  keepalive, and runtime password memory. See
  [`docs/SSH_ROADMAP.md`](docs/SSH_ROADMAP.md) and
  [`docs/native-ssh/`](docs/native-ssh/).

#### Ongoing guardrails

- **Rendering performance contract** — snapshot-only rendering boundary,
  replay parity, seam safety under fractional DPI, and conservative perf
  ceilings enforced by CI. See
  [`docs/RENDERING_PERF_CONTRACT.md`](docs/RENDERING_PERF_CONTRACT.md).
  Historical design context:
  [`docs/gpu-hardening/`](docs/gpu-hardening/).
- **Configuration storage contract** — where user state lives, what an update
  versus an uninstall deletes, why the Velopack `packId` deliberately differs
  from the app name, and why secrets are not in the config folder. Read before
  building anything that reads or writes user configuration (backup/restore,
  export/import, sync, migrations). See
  [`docs/CONFIG_STORAGE_CONTRACT.md`](docs/CONFIG_STORAGE_CONTRACT.md).

---

### Build & test

Prerequisites:

- .NET 10 SDK. The solution targets `net10.0`.
- Rust stable toolchain installed via `rustup`. Both native crates use Rust edition 2024, so `rustc` and `cargo` must be on `PATH`.
- macOS: Xcode Command Line Tools (`xcode-select --install`) so Cargo has an available system linker.
- Windows: Rust's default `stable-x86_64-pc-windows-msvc` toolchain expects the MSVC build tools to be installed.

Verify the toolchain before building:

```bash
dotnet --version
rustc --version
cargo --version
```

Notes:

- `dotnet build` for `src/NovaTerminal.App` triggers `cargo build --release` for the native PTY and native SSH libraries automatically.
- The CLI project references the app project, so `dotnet build` and `dotnet test` both require the Rust toolchain unless you explicitly set `SKIP_RUST_NATIVE_BUILD=1` for a downstream job that already has the native artifacts.
- If a clean clone fails during Cargo's `build-script-build` step on macOS, first confirm `rustc`/`cargo` are installed and that Xcode Command Line Tools are available. If the failure happened after a partial build, remove `src/NovaTerminal.App/native/target` and `src/NovaTerminal.App/native/rusty_ssh/target` and retry.

Build — **always through the wrapper scripts**, never raw `dotnet build`:

```bash
# Linux / macOS / Git Bash
scripts/build.sh build -c Release
```

```powershell
# Windows / PowerShell
scripts\build.ps1 build -c Release
```

The wrappers pass `-nodeReuse:false` and set `DOTNET_CLI_USE_MSBUILD_SERVER=0`.
Without them, MSBuild leaves daemons holding the caller's stdout/stderr handles,
and any build whose output is captured by a parent process (CI runners, agents,
test harnesses) hangs indefinitely — usually looking stuck in `BuildCliShim`.
Details in [`CLAUDE.md`](CLAUDE.md).

Run tests (same filter as the gating CI unit lane):

```bash
scripts/build.sh test -c Release --filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng"
```

CI applies that filter per test project rather than across the solution, and
runs the App.Tests `Lane=PlatformBoot` tests in a separate process. Per-project
runs are also the fast local loop — a whole-solution run takes tens of minutes
because of the headless Avalonia suite.

Use `ci/run.sh` (Linux/macOS) or `ci/run.ps1` (Windows) for the full local
CI-style sequence. Both scripts assume the .NET and Rust toolchains are already installed.

### Native AOT publish

NovaTerminal is configured for **Native AOT** publish in
[`src/NovaTerminal.App/NovaTerminal.App.csproj`](src/NovaTerminal.App/NovaTerminal.App.csproj).
The project supports `win-x64`, `linux-x64`, and `osx-arm64` publish targets.
The release workflow publishes Native AOT bundles for those targets to the
corresponding GitHub Release.

Example publish command:

```bash
dotnet publish src/NovaTerminal.App/NovaTerminal.App.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts/publish/win-x64
```

Swap `win-x64` for `linux-x64` or `osx-arm64` as needed.

---

### Running GitHub CI locally with `act`

NovaTerminal workflows exchange artifacts between jobs (native binaries and
test results). When running via `act`, enable its artifact server or
artifact upload/download steps will fail.

Recommended command:

```bash
act pull_request -P ubuntu-latest=catthehacker/ubuntu:act-latest --artifact-server-path .act-artifacts
```

Notes:

- `--artifact-server-path` is required for `actions/upload-artifact` / `actions/download-artifact`.
- To bypass Rust rebuild inside downstream .NET jobs, set `--env SKIP_RUST_NATIVE_BUILD=1`.

---

### Project status

Under active development. Current focus and upcoming milestones are tracked
in [`docs/ROADMAP.md`](docs/ROADMAP.md).

License: [`MIT`](LICENSE).

---

### Contributing

Contributions are welcome. NovaTerminal has a strong correctness culture —
terminal core invariants are enforced and automated tests gate changes. See
[`CONTRIBUTING.md`](CONTRIBUTING.md) for details, and
[`docs/reviews/`](docs/reviews/) for periodic deep code reviews with the
current known-issues backlog.

---

### Acknowledgements

Thanks to [**Greptile**](https://www.greptile.com/) for granting NovaTerminal
free access to their AI code review as an open source project. It reviews every
pull request here, and that extra pair of eyes is a real help on a codebase
where correctness is the whole point.

---

## Philosophy

NovaTerminal aims to be:

- **boring in behavior**
- **predictable under stress**
- **fast without shortcuts**
- **cross-platform without divergence**

A terminal you can trust.
