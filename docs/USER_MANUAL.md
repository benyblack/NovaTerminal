# NovaTerminal User Manual

Welcome to NovaTerminal, a modern, cross-platform terminal emulator focused on correctness, performance, and predictability. This manual covers every feature currently available to help you maximize your productivity.

## 1. Getting Started
### 1.1 Command Palette
The **Command Palette** is the central hub for accessing all features and commands in NovaTerminal.
- **Shortcut:** `Ctrl+Shift+P`
- **Usage:** Type any feature name to filter and execute commands instantly.

### 1.2 Settings & Customization
- **Open Settings:** Access settings via the Command Palette (`Settings`). Changes apply live without requiring a restart.
- **Themes:** Switch between `Solarized Dark` and `Default (Dark)` via the palette.
- **Font & Sizing:** Increase (`Ctrl++`) or decrease (`Ctrl+-`) font sizes. You can also customize your preferred font family in Settings.

---

## 2. Window and Tab Management
NovaTerminal offers advanced windowing capabilities, including tabs, workspaces, and workspace bundles.

### 2.1 Tab Basics
- **New Tab:** `Ctrl+Shift+T` (Opens the default profile)
- **Close Tab:** `Ctrl+W`
- **Switch Tabs (MRU):** Use `Ctrl+Tab` for the next tab and `Ctrl+Shift+Tab` for the previous tab in Most Recently Used order.
- **Open Tab List:** `Ctrl+Shift+O` (Useful when you have many tabs hidden in overflow).

### 2.2 Advanced Tab Actions
- **Rename:** Use `Tab: Rename Current` to set a custom title.
- **Pin & Protect:** Use `Tab: Toggle Pin` to pin a tab and `Tab: Toggle Protect` to protect it from accidental closure.
- **Copy Title:** `Tab: Copy Current Title`
- **Close Others:** `Tab: Close Others` removes all tabs except the currently active one.

### 2.3 Workspaces & Templates
Workspaces save your exact window state, including tabs, pane splits, and zooming.
- **Save/Load:** `Workspace: Save Current` and `Workspace: Load...`
- **Templates:** Save reusable layouts via `Workspace Template: Save Current` and apply them with `Workspace Template: Apply...`.
- **Profile Rules:** Automatically apply a template whenever a specific profile launches (`Tab Rule: Set Template for Current Profile...`).

### 2.4 Workspace Bundles (Portable Sessions)
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

## 4. Remote Connections (SSH & SFTP)
NovaTerminal supports high-performance SSH sessions integrated directly into the terminal, with built-in remote file management.

### 4.1 SSH Profiles and Connection Manager
- Easily maintain local and SSH profiles in your Settings.
- **Security:** Credentials use secure platform vault backends. No unexpected password injections triggered by terminal output are allowed for your safety. Fast reconnects and config caching simplify remote work.

#### SSH backends

NovaTerminal ships two SSH backends:

- **OpenSSH** (default) — drives the system `ssh` binary. This is the supported path for everyday use.
- **Native SSH** (experimental) — an in-process SSH client with its own host-key trust store, port forwarding, and one-hop jump support. Enable via the experimental toggle in Settings. The native backend does not silently fall back to OpenSSH if it fails.

Current native-backend limitations: no remote forwarding, no multi-hop jump chains, and no dynamic forwarding through a jump host. See `docs/SSH_ROADMAP.md` for details.

### 4.2 Built-in SFTP Transfers
Access the following commands via the palette to transfer files and folders between your local machine and the SSH host:
- `SFTP: Upload File...` / `SFTP: Upload Folder...`
- `SFTP: Download File...` / `SFTP: Download Folder...`
- `SFTP: Show Transfers`: Toggles the Transfer Center overlay to monitor ongoing transfers.

*(Note: SFTP commands only function when the active pane is an SSH session).*

Transfer behavior depends on the SSH backend:

- **OpenSSH profiles** use the system `scp` executable.
- **Native SSH profiles** use NovaTerminal's built-in native SFTP path for file and folder upload/download.

Current Native SSH transfer notes:

- Transfers use the native backend's known-hosts store and do not silently fall back to OpenSSH.
- Password and identity-file authentication are supported for non-interactive transfers.
- Cancellation is supported from the Transfer Center for native transfers.

---

## 5. Terminal Engine & UI Behavior

### 5.1 Scrolling and Cursor
- **Smooth Scrolling:** Toggle via `Scroll: Toggle Smooth`.
- **Cursor Styles:** Choose between `Cursor: Block`, `Cursor: Beam`, or `Cursor: Underline`.
- **Cursor Blink:** Toggle blinking on and off (`Cursor: Toggle Blink`). Note that TUI apps like `vim` or `yazi` may control their own blinking phase.

### 5.2 Visual & Audio Feedback
- **Audio Bell:** `Bell: Toggle Audio`.
- **Visual Bell:** `Bell: Toggle Visual Flash`.
- **Tab Indicators:** Tabs show status icons such as `•` (background activity), `🔔` (bell/attention), `✓` / `✖` (exit status), `📌` (pinned), and `🔒` (protected).

### 5.3 Advanced Graphics Support
NovaTerminal supports rendering rich images inline natively:
- **Sixel Graphics** (via `libsixel`, `lsix`)
- **iTerm2 Inline Images** (via `imgcat`)
- **Kitty Graphics Protocol**

---

## 6. Exporting and Debugging
Tools designed for diagnosing visual issues, performance profiling, and saving session output.

### 6.1 Snapshot Export
Export the current terminal state containing text, colors, and styles.
- **Plain Text:** `Pane: Export Snapshot (Plain Text)`
- **ANSI:** `Pane: Export Snapshot (ANSI)` (Preserves original styling & colors).
- **PNG:** `Pane: Export Snapshot (PNG)`

### 6.2 Render Performance HUD
- **Toggle Render HUD:** Enables a real-time overlay showing frame time, dirty rows/cells, draw calls, and glyph cache hit rates. Use this when experiencing degraded visual performance.

### 6.3 Debug Screens
- **Box Drawing Test:** Accessible via `Debug: Box Drawing Test Screen` to verify font rendering, gaps, and line alignments.

### 6.4 VT Conformance Report CLI

NovaTerminal ships a machine-readable VT conformance report derived from its
coverage matrix. Run the terminal executable with:

- `NovaTerminal.Cli --vt-report` — concise summary (matrix path, support-status counts, validation counts).
- `NovaTerminal.Cli --vt-report --json` — full machine-readable JSON report.

On Windows, prefer the console-side executable for interactive shell use. The GUI app `NovaTerminal.exe` is intended for normal windowed startup, while `NovaTerminal.Cli.exe` is the reliable VT-report entrypoint from PowerShell or `cmd`.

This is useful when filing compatibility bug reports or comparing against
another terminal emulator's claims.
