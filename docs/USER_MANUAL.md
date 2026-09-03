# NovaTerminal User Manual

Welcome to NovaTerminal, a modern, cross-platform terminal emulator focused on correctness, performance, and predictability. This manual covers every feature currently available to help you maximize your productivity.

## 1. Getting Started
### 1.1 Command Palette
The **Command Palette** is the central hub for accessing all features and commands in NovaTerminal.
- **Shortcut:** `Ctrl+Shift+P`
- **Usage:** Type any feature name to filter and execute commands instantly.

### 1.2 Settings & Customization
- **Open Settings:** `Ctrl+,` or `Settings` in the palette. Changes apply live
  without requiring a restart. Pages: Appearance, Profiles, Shortcuts, Command
  Assist, Agent Access, SSH, Backup.
- **Themes:** Fourteen themes ship built in — Default, Dracula, Nord, Gruvbox Dark,
  Tokyo Night, Catppuccin Mocha, Solarized Dark/Light, GitHub Dark/Light, Monokai,
  OneHalf Dark/Light and Cobalt2. Switch with `Theme: <name>` in the palette or from
  Settings → Appearance, and import your own theme JSON there too.
- **Font & Sizing:** Increase (`Ctrl++`) or decrease (`Ctrl+-`) font sizes. You can also customize your preferred font family in Settings.
- **Shortcuts:** Every shortcut in this manual is the default. Settings → Shortcuts
  lists them all and lets you rebind them.

---

## 2. Window and Tab Management
NovaTerminal offers advanced windowing capabilities, including tabs, workspaces, and workspace bundles.

### 2.1 Tab Basics
- **New Tab:** `Ctrl+Shift+T` (Opens the default profile)
- **Close Tab:** `Ctrl+W`
- **Switch Tabs (MRU):** Use `Ctrl+Tab` for the next tab and `Ctrl+Shift+Tab` for the previous tab in Most Recently Used order.
- **Open Tab List:** `Ctrl+Shift+O` (Useful when you have many tabs hidden in overflow).
- **Reorder:** Drag a tab along the strip, or use `Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown`.
- **Vertical Sidebar:** `Ctrl+Shift+L` swaps the top tab strip for a left sidebar — see 2.3.

### 2.2 Advanced Tab Actions
- **Rename:** Use `Tab: Rename Current` to set a custom title.
- **Pin & Protect:** Use `Tab: Toggle Pin` to pin a tab and `Tab: Toggle Protect` to protect it from accidental closure.
- **Copy Title:** `Tab: Copy Current Title`
- **Close Others:** `Tab: Close Others` removes all tabs except the currently active one.

### 2.3 Vertical Tab Sidebar
Toggle with `Ctrl+Shift+L`, `Tabs: Toggle Vertical Tab Sidebar` in the palette, or
Settings → Appearance → **Tab strip orientation**. Vertical mode trades horizontal
space for a much richer row per tab, which is what you want when tabs are running
long jobs or agents rather than sitting idle.

Each row shows the tab title, a status dot, any marker chips, and the tab's most
recent non-empty output line as a live preview.

The **status dot** paints exactly one state, in this precedence:

| Dot | Meaning |
|---|---|
| Amber | A bell fired, or the tab wants attention |
| Amber (agent) | An agent typed into this tab and you have not looked yet |
| Blue | Output is streaming, or a command is still running |
| Blue (agent) | An agent is reading this tab (only under the "All" rollup policy) |
| *(none)* | Idle |

**Marker chips** trail the title and, unlike the dot, can stack — bell, plain
activity, agent-wrote and agent-watched are tracked separately. Bell and plain
activity are the one mutually exclusive pair. Attention markers clear when you
activate the tab.

Other sidebar behavior:

- **Resize:** drag the sidebar's right edge. The width is remembered.
- **Overflow:** when rows run past the bottom of the sidebar, a pill pinned to the
  bottom edge shows how many tabs are hidden and opens a list of them.
- **Reorder:** drag a row, or use `Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown`.

### 2.4 Workspaces & Templates
Workspaces save your exact window state, including tabs, pane splits, and zooming.
- **Save/Load:** `Workspace: Save Current` and `Workspace: Load...`
- **Templates:** Save reusable layouts via `Workspace Template: Save Current` and apply them with `Workspace Template: Apply...`.
- **Profile Rules:** Automatically apply a template whenever a specific profile launches (`Tab Rule: Set Template for Current Profile...`).

### 2.5 Workspace Bundles (Portable Sessions)
Bundles allow exporting and importing tabs and pane layouts as portable `.novaws.json` files.
- **Exporting:** `Workspace: Export Bundle...` or `Workspace: Export Current Session Bundle...`
- **Import/Open:** `Workspace: Import Bundle...` or `Workspace: Open Bundle...`
*(Note: Enterprise policies may restrict bundle sharing in managed environments).*

---

## 3. Panes and Layouts
Panes allow you to split a single tab into multiple terminal windows.

### 3.1 Splitting and Navigation
- **Split Vertical:** `Ctrl+Shift+D` (Places a new pane side-by-side).
- **Split Horizontal:** `Ctrl+Shift+E` (Places a new pane below).
- **Close Pane:** `Ctrl+Shift+W` (Closes only the active pane, leaving others open).
- **Navigation:** Use `Alt+Left`, `Alt+Right`, `Alt+Up`, `Alt+Down` to move focus between panes.
- **Equalize:** `Ctrl+Shift+G` resets pane sizes to be equal.

### 3.2 Advanced Pane Features
- **Zoom Pane:** `Ctrl+Shift+Z` toggles zooming of the active pane to fill the entire tab temporarily.
- **Broadcast Input:** `Ctrl+Shift+B` toggles sending keystrokes to *all* panes in the current tab simultaneously.
- **Find/Search:** `Ctrl+Shift+F` opens the search overlay for the active pane.

---

## 4. Command Assist

Command Assist suggests completions, recalls history, and proposes fixes. It is
anchored to what the shell actually reports through OSC 133 shell-integration
marks rather than to a guess about where the prompt ends, and it reads the live
command line from the terminal grid rather than from a shadow copy of your
keystrokes — so it stays correct through re-prompts, wrapped lines and edits.

Everything below runs locally. There is no network call.

### 4.1 Using it
- **Toggle:** `Ctrl+Space`
- **History search:** `Ctrl+R` — accepting a row replaces what you had typed
- **Help:** `Ctrl+Shift+H`
- **Pin / unpin the selection as a snippet:** `Ctrl+Shift+S`
- **While the surface is open:** `Up` / `Down` to browse, `Enter` to accept the
  browsed row, `Ctrl+Enter` to insert it without running, `Escape` to close

A passive suggestion bubble appears as you type; its hint strip shows the current
key names, so it stays honest if you rebind them in Settings → Shortcuts. The
whole surface auto-hides while a fullscreen TUI owns the grid.

### 4.2 Fix mode
When a command fails, Command Assist can propose a correction derived from that
command's *own* output rather than from a generic rule — it captures the failing
output for exactly this purpose.

### 4.3 Knowledge and snippets
- A tldr-derived command catalogue supplies descriptions and common invocations.
- Pinning turns any suggestion or history entry into a saved snippet; snippets are
  managed from the same surface.
- History is stored locally as append-only JSONL and scoped to the context you ran
  the command in.

### 4.4 Shell integration
Marks come from a small shell hook (bash, zsh, fish and PowerShell are supported).
Settings → Command Assist offers a **one-line installer** you can paste on a remote
host, and mark detection works over SSH. In sessions that emit no marks at all,
Command Assist still captures straight-through-typed commands — you lose the
anchoring, not the feature.

---

## 5. Agent Output Panel

Coding agents print a lot of markdown into a terminal, where it renders as raw
`##` and backticks. The Agent Output panel renders that output properly, beside
the pane, while it streams.

### 5.1 Opening it
An **MD** button fades in at the top-right of a pane when its recent output looks
like markdown; click it to open the panel. If you open the panel with nothing
tracked yet, it snapshots the latest response already on screen (prompt lines
trimmed) so you are not looking at an empty pane.

The panel is per-pane, and it stays down while a fullscreen program owns the grid
— reopening on its own when you leave. Your toggle survives that suppression.

### 5.2 The panel header
- **Render** — renders fenced blocks as formatted nested documents. Turn it off to
  see fences as plain code. Diff fences get diff-aware rendering either way.
- **Copy** — copies the raw markdown source, not the rendered text.
- **✕** — closes the panel.

---

## 6. Remote Connections (SSH & SFTP)
NovaTerminal supports high-performance SSH sessions integrated directly into the terminal, with built-in remote file management.

### 6.1 SSH Profiles and Connection Manager
- Open the **Connection Manager** with `Ctrl+Shift+K` (or the toolbar button). It is a
  real window, so you can leave it open alongside the terminal, and it can show,
  clear, or delete a profile's saved password.
- Easily maintain local and SSH profiles in your Settings.
- **Security:** Credentials use secure platform vault backends. No unexpected password injections triggered by terminal output are allowed for your safety. Fast reconnects and config caching simplify remote work.

#### SSH backends

NovaTerminal ships two SSH backends:

- **Native SSH** — an in-process SSH client with its own host-key trust store. It is
  the **default for new profiles**, and supports password, identity-file and
  ssh-agent authentication, jump chains of any length, and local, remote and dynamic
  port forwarding. It does not silently fall back to OpenSSH if it fails.
- **OpenSSH** — drives the system `ssh` binary. Still selectable per profile, and
  still the default for profiles you created before the switch.

Settings → SSH holds the global native toggle. With it off, new profiles default to
OpenSSH instead, so the default can never point at a backend that will refuse to
run. NovaTerminal warns you if a native profile carries mux options or extra SSH
arguments that only the OpenSSH backend understands. See `docs/SSH_ROADMAP.md` for
the full capability matrix.

### 6.2 Built-in SFTP Transfers
Access the following commands via the palette to transfer files and folders between your local machine and the SSH host:
- `SFTP: Upload File...` / `SFTP: Upload Folder...`
- `SFTP: Download File...` / `SFTP: Download Folder...`
- `SFTP: Show Transfers`: Toggles the Transfer Center overlay to monitor ongoing transfers.

*(Note: SFTP commands only function when the active pane is an SSH session).*

Transfer behavior depends on the SSH backend:

- **OpenSSH profiles** use the system `scp` executable.
- **Native SSH profiles** use NovaTerminal's built-in native SFTP path for file and folder upload/download.

Native SSH panes also expose a pane-local `Remote Files` sidebar from the pane context menu. The sidebar is lightly navigable, opens from the pane's current remote directory when available, and keeps upload/download actions grounded in the directory or entry you are looking at. The compact rail shows the active host identity, current remote path, and per-entry modified dates so you can quickly spot recently changed files. `Upload File` and `Upload Folder` target the directory currently shown in the sidebar, while `Download Selected` uses the selected remote file or folder.

Current Native SSH transfer notes:

- Transfers use the native backend's known-hosts store and do not silently fall back to OpenSSH.
- Password and identity-file authentication are supported for non-interactive transfers.
- The transfer dialog offers remote path autocomplete when the same profile already has an active Native SSH session.
- Sidebar-initiated uploads go straight to a local file or folder picker, then start the transfer directly into the sidebar's current remote directory.
- Sidebar-initiated file downloads use a save picker with the remote filename prefilled; sidebar folder downloads use a local folder picker.
- Manual `SFTP:` command-palette flows still use the transfer dialog, including remote path autocomplete when an active Native SSH session is available.
- The `Remote Files` sidebar hides automatically while alternate-screen/fullscreen terminal applications are active.
- Single-file transfers show live byte progress when the total size is known.
- Folder transfers report per-file progress callbacks; they do not show a precomputed total for the entire tree.
- Cancellation is supported from the Transfer Center for native transfers.

---

## 7. Terminal Engine & UI Behavior

### 7.1 Scrolling and Cursor
- **Smooth Scrolling:** Toggle via `Scroll: Toggle Smooth`.
- **Cursor Styles:** Choose between `Cursor: Block`, `Cursor: Beam`, or `Cursor: Underline`.
- **Cursor Blink:** Toggle blinking on and off (`Cursor: Toggle Blink`). Note that TUI apps like `vim` or `yazi` may control their own blinking phase.

### 7.2 Visual & Audio Feedback
- **Audio Bell:** `Bell: Toggle Audio`.
- **Visual Bell:** `Bell: Toggle Visual Flash`.
- **Tab Indicators:** Tabs show status icons such as `•` (background activity), `🔔` (bell/attention), `✓` / `✖` (exit status), `📌` (pinned), and `🔒` (protected). The vertical sidebar shows a richer set, including agent activity — see 2.3.

### 7.3 Advanced Graphics Support
NovaTerminal supports rendering rich images inline natively:
- **Sixel Graphics** (via `libsixel`, `lsix`)
- **iTerm2 Inline Images** (via `imgcat`)
- **Kitty Graphics Protocol**

---

## 8. Configuration Backup & Restore

Export your NovaTerminal configuration to a single portable `.novabackup` file and
import it on another machine. Open **Settings → Backup**, or use `Export
configuration…`, `Import configuration…` or `Restore from snapshot…` in the palette
— all three route to that page, because export and import need a file picker and a
mode prompt, and restore needs its confirmation.

### 8.1 What a bundle contains
Six independently selectable categories: **Settings**, **Themes**, **Connections**,
**Workspaces**, **Policy** and **Snippets**. A bundle is a zip with a manifest, so
you can inspect one before importing it.

> Connection **passwords are not in a bundle**. They live in the OS credential
> store, not in the config folder, so imported SSH profiles need their passwords
> re-entered on first connect. A bundle does carry each profile's
> "remember password" preference.

### 8.2 Import modes and snapshots
Import either **merges** into your current configuration or **replaces** it.

NovaTerminal also takes automatic snapshots in the background: it watches the
backed-up paths and writes a snapshot once changes go quiet, deduplicated by
content hash and capped by a retention limit. A snapshot is taken immediately
before every import and every restore, so both are reversible from
**Restore from snapshot…**.

### 8.3 From the command line
```
NovaTerminal.Cli backup export <path>
NovaTerminal.Cli backup import <path> --merge | --replace
NovaTerminal.Cli backup list
NovaTerminal.Cli backup restore <id>
```

Agents get read-only `export` and `list` through MCP, never import or restore.

---

## 9. Agent Access (MCP)

NovaTerminal can expose your live terminal sessions to AI coding agents over a
local [Model Context Protocol](https://modelcontextprotocol.io) server. **Every
part of this is off by default** — with the toggles off there is no live endpoint
at all, and the server still answers the offline repo/documentation tools.

Settings → **Agent Access**:

- **Agent access (observe)** — the master switch. Lets an agent list sessions, read
  the screen and scrollback, query status, and wait for events.
- **Replay export** — additionally lets an agent save a session's recent output as a
  replay file. Exports contain output and window resizes only, never anything you
  type. Requires observe.
- **Agent screenshots** — additionally lets an agent render a pane to a PNG. A
  picture shows everything drawn in the pane, inline images included. Requires
  observe; every capture is journaled.
- **Agent access (act)** — additionally lets an agent type into, open and close
  sessions. SSH connections must *also* be allowlisted individually. Requires
  observe; every action is journaled.

### 9.1 Seeing what an agent did
Panes carry a live indicator while an agent is observing or acting on them, and
every acting call — allowed or denied — plus every screenshot is recorded in the
**agent activity journal**. Open it from the title-bar menu → **Agent Activity...**,
or put the Agent Activity button on the title bar from Settings → Appearance.

For an agent's own view of a session, `export_replay` plus
`NovaTerminal.Cli --replay <file>` re-renders it deterministically, frame by frame.

---

## 10. Exporting and Debugging
Tools designed for diagnosing visual issues, performance profiling, and saving session output.

### 10.1 Snapshot Export
Export the current terminal state containing text, colors, and styles.
- **Plain Text:** `Pane: Export Snapshot (Plain Text)`
- **ANSI:** `Pane: Export Snapshot (ANSI)` (Preserves original styling & colors).
- **PNG:** `Pane: Export Snapshot (PNG)`

### 10.2 Session Recording
Capture the active pane's session to a replayable recording.
- **Toggle Recording:** `Ctrl+Shift+R` (or the toolbar record button, or `Toggle Recording` in the command palette) starts/stops recording the active pane.
- **Open a recording:** `Open Recording...` from the command palette.
- **Browse recordings:** `Open Recordings Folder`.

### 10.3 Render Performance HUD
- **Toggle Render HUD:** Enables a real-time overlay showing frame time, dirty rows/cells, draw calls, and glyph cache hit rates. Use this when experiencing degraded visual performance.

### 10.4 Debug Screens
- **Box Drawing Test:** Accessible via `Debug: Box Drawing Test Screen` to verify font rendering, gaps, and line alignments.

### 10.5 VT Conformance Report CLI

NovaTerminal ships a machine-readable VT conformance report derived from its
coverage matrix. Run the terminal executable with:

- `NovaTerminal.Cli --vt-report` — concise summary (matrix path, support-status counts, validation counts).
- `NovaTerminal.Cli --vt-report --json` — full machine-readable JSON report.

On Windows, prefer the console-side executable for interactive shell use. The GUI app `NovaTerminal.exe` is intended for normal windowed startup, while `NovaTerminal.Cli.exe` is the reliable VT-report entrypoint from PowerShell or `cmd`.

This is useful when filing compatibility bug reports or comparing against
another terminal emulator's claims.

---

## 11. Updates

How NovaTerminal updates depends on how you installed it:

- **Windows installer** and the **macOS `.pkg`** check in the background. A new
  version downloads quietly and is applied when you accept the prompt and restart —
  never a surprise restart. On macOS, if the app lives in `/Applications`, the OS
  asks for your password once per update.
- **Linux AppImage** updates itself by rewriting the AppImage, so keep it somewhere
  writable such as `~/Applications`.
- **Debian/Ubuntu package** installs update through your package manager; the in-app
  updater is inactive there by design.
- **Portable zip / tarball** builds do not update themselves.

From the palette: `Update: Check for updates`, and once a version is staged,
`Update: Restart to apply <version>`. Automatic checks can be turned off in
Settings.
