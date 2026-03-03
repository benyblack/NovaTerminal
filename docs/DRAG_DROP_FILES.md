Epic: Drag & Drop file support for interactive CLI (NovaTerminal)
=================================================================

Epic goals
----------

-   Drag/drop files onto terminal surface inserts **shell-correct escaped paths** at cursor.

-   Safe defaults (don't leak into password prompts).

-   Optional "Paste text file contents (bracketed paste)" action for LLM CLIs (Codex/Gemini).

Non-goals (for now)
-------------------

-   Uploading files to remote hosts automatically (scp/rsync).

-   Perfect "current shell input buffer" introspection (template rules can come later with shell integration).

* * * * *

Milestone M0: Drop → Insert escaped path(s)
===========================================

User story
----------

As a user, when I drag files into NovaTerminal, it inserts their paths into the current prompt line, properly quoted for my shell, so I can run commands like `codex -f <path>` quickly.

Tasks (issues)
--------------

### M0.1 --- TerminalView drag/drop plumbing (Avalonia)

**Description**

-   Handle `DragEnter`, `DragOver`, `Drop` on the terminal surface control.

-   Extract file paths from drag data.

**Acceptance criteria**

-   Dropping a file anywhere on the terminal surface triggers a handler and yields a list of absolute local paths.

-   Supports multi-file drops.

**Notes**

-   Use platform APIs that Avalonia exposes for file drops (storage items / file names).

* * * * *

### M0.2 --- SessionContext: determine quoting target (shell/environment)

**Description**\
Introduce a lightweight `SessionContext` used by drop logic:

-   `DetectedShell`: `Pwsh`, `Cmd`, `PosixSh` (bash/zsh), `Unknown`

-   `IsWslSession`: bool

-   `IsEchoEnabled`: bool (from terminal modes)

-   `IsAltScreen`: bool (from VT state if available)

**Acceptance criteria**

-   Shell is detected best-effort:

    -   Windows local: pwsh vs cmd

    -   mac/linux: posix

    -   Unknown falls back to posix quoting or user-configured default

-   Exposes "secure input" signal via ECHO mode (see M0.4).

* * * * *

### M0.3 --- Shell quoting module

**Description**\
Implement `IShellQuoter.QuotePath(string path) -> string` and a helper to join many paths.

Quoters:

-   `PwshQuoter`: single-quote strategy; escape `'` as `''`

-   `CmdQuoter`: wrap in `"..."` and escape internal `"` appropriately (minimal safe approach)

-   `PosixShQuoter`: safe single-quote strategy: `'foo'\''bar'`

**Acceptance criteria**

-   Unit tests cover tricky strings:

    -   spaces

    -   `'` and `"`

    -   `&` `|` `$` `!` `(` `)` `;`

    -   unicode characters

-   Output is syntactically valid for the target shell.

* * * * *

### M0.4 --- Safety gate: don't inject into no-echo

**Description**\
If the terminal mode indicates ECHO is off (common for password prompts), block drop insertion by default.

**Acceptance criteria**

-   When `IsEchoEnabled == false`, drop does nothing and shows a small toast/status message:

    -   "Drop blocked (secure input). Hold Alt to force." (or similar)

-   Holding a modifier (Alt) bypasses the block.

* * * * *

### M0.5 --- Insert at cursor via PTY input (no clipboard)

**Description**\
Send the quoted paths to the PTY as if typed. Insert should respect the terminal's current cursor focus (i.e., "active pane").

**Acceptance criteria**

-   Dropped path appears in the terminal input stream at the current prompt location (not copied to clipboard).

-   Multi-file drop inserts `path1 path2 path3`.

-   Works in split panes: goes to the active pane/session.

* * * * *

### M0.6 --- Minimal config knob (optional but recommended)

**Description**\
Add per-profile setting: `ShellOverride = Auto|Pwsh|Cmd|Posix`.

**Acceptance criteria**

-   User can force shell quoting behavior when detection fails.

* * * * *

M0 test plan
------------

### Unit

-   `QuoterTests` for pwsh/cmd/posix.

-   `DropRouterTests` for "secure input blocks unless Alt".

### Integration

-   Spawn a PTY with pwsh/cmd/bash and simulate drop through router; assert bytes written.

* * * * *

Milestone M1: Text-file smart action (Paste contents via bracketed paste)
=========================================================================

User story
----------

As a user running Codex/Gemini in the terminal, I can drop a `.md`/`.txt` and choose "Paste contents" so the prompt receives the file content safely.

Tasks (issues)
--------------

### M1.1 --- Text detection & size limits

**Description**\
Heuristic: treat as text if:

-   file size <= configurable cap (default 256KB)

-   UTF-8 decode succeeds with low replacement ratio / no NUL bytes

**Acceptance criteria**

-   Binary files do not offer "Paste contents".

-   Large files do not auto-read; only insert path.

* * * * *

### M1.2 --- Bracketed paste sender

**Description**\
Implement sending:

-   `ESC[200~` + content + `ESC[201~`

**Acceptance criteria**

-   "Paste contents" sends bracketed-paste wrapped bytes.

-   Works in bash/pwsh REPLs without mangling.

-   Newline strategy: preserve file newlines by default (optional normalize setting later).

* * * * *

### M1.3 --- UX: toast/action strip on drop of text file

**Description**\
After inserting the path (still default), show an action:

-   `[Paste contents]` (and maybe `[Copy contents]` if you already have a clipboard service)

**Acceptance criteria**

-   Action appears only for eligible text files.

-   Clicking action triggers the bracketed paste sender.

-   No modal dialogs.

* * * * *

M1 test plan
------------

-   Unit: text detection (NUL byte, invalid UTF-8, size threshold)

-   Integration: bracketed paste sequences appear in PTY stream

* * * * *

(Future) M2: WSL path mapping
=============================

-   Use `wsl.exe wslpath -a -u "<winpath>"` with caching.

-   Map only when `IsWslSession == true`.

(Future) M3: Drop Template Rules + optional shell integration
=============================================================

-   Rule engine needs *actual command line buffer*; best done via shell integration emitting OSC metadata.

* * * * *

Proposed file / module map
==========================

### UI layer

-   `src/NovaTerminal.App/Views/TerminalView.axaml.cs` (or wherever terminal control lives)

    -   drag/drop events → `DropRouter.HandleDrop(...)`

### Core / services

-   `src/NovaTerminal.Core/Input/DropRouter.cs`

-   `src/NovaTerminal.Core/Input/TerminalInputSender.cs` (writes to PTY)

-   `src/NovaTerminal.Core/Sessions/SessionContext.cs`

-   `src/NovaTerminal.Core/Shell/IShellQuoter.cs`

-   `src/NovaTerminal.Core/Shell/PwshQuoter.cs`

-   `src/NovaTerminal.Core/Shell/CmdQuoter.cs`

-   `src/NovaTerminal.Core/Shell/PosixShQuoter.cs`

-   `src/NovaTerminal.Core/VT/TerminalModes.cs` (or wherever ECHO mode is tracked)

-   `src/NovaTerminal.Core/UI/ToastService.cs` (or existing notification mechanism)

### Tests

-   `tests/NovaTerminal.Tests/ShellQuoterTests.cs`

-   `tests/NovaTerminal.Tests/DropRouterTests.cs`

(Adapt names to your repo layout; the structure is what matters.)

Milestone M2: WSL path mapping (Windows host → WSL shell)
=========================================================

Goal
----

When the active terminal session is **WSL** (bash/zsh/etc inside WSL), drag-dropping Windows files should insert **WSL paths** (e.g. `/mnt/c/...`) instead of `C:\...`.

Example:

-   Drop: `C:\Users\Behnam\code\prompt.md`

-   Insert: `'/mnt/c/Users/Behnam/code/prompt.md'` (quoted per POSIX rules)

* * * * *

M2 Issues / Tasks
=================

M2.1 --- Detect WSL session reliably
----------------------------------

**Approach (best-effort; layered)**

1.  **If PTY child process is `wsl.exe`** → session is WSL.

2.  If process args include `--distribution` / `-d` / `--exec` etc → still WSL.

3.  If environment indicates WSL (less reliable because it's *inside* WSL, not host) --- optional.

**Acceptance criteria**

-   WSL sessions are detected correctly for common launch modes:

    -   `wsl.exe`

    -   `wsl.exe -d Ubuntu`

    -   `wsl.exe --exec bash -lc ...`

* * * * *

M2.2 --- Implement Windows → WSL path mapper
------------------------------------------

**Mechanism**

-   Call: `wsl.exe wslpath -a -u "<winpath>"`

    -   `-a`: absolute

    -   `-u`: Unix style

-   Use the **same distribution** as the session if known:

    -   `wsl.exe -d <Distro> wslpath -a -u "<winpath>"`

**Acceptance criteria**

-   For a session in distro "Ubuntu", mapping uses Ubuntu's view.

-   Returns `/mnt/<driveLower>/...` for normal paths (typical).

-   Handles spaces/unicode.

**Failure handling**

-   If `wslpath` fails:

    -   fallback to original Windows path (still quoted, but less useful)

    -   show a toast once per session: "WSL path mapping failed; inserted Windows path."

* * * * *

M2.3 --- Cache path mapping per session
-------------------------------------

**Why**: People drop the same file repeatedly; also avoids spawning `wsl.exe` too often.

**Design**

-   Cache key: `(distroName?, winPathNormalized)`

-   Value: `wslPath`

-   Size limit: e.g. 512 entries per session (LRU)

-   Normalize win path for key:

    -   full path

    -   consistent casing on drive letter (upper/lower)

    -   trim trailing separators

**Acceptance criteria**

-   Re-dropping the same path does not re-invoke `wsl.exe` (verify via debug logs or a counter in tests).

* * * * *

M2.4 --- Integrate mapping into DropRouter
----------------------------------------

**Pipeline**

1.  DropRouter receives file paths

2.  If session context says WSL:

    -   map each path via `IPathMapper` into WSL path

    -   then quote with `PosixShQuoter`

3.  Else:

    -   quote as before (pwsh/cmd/posix)

**Acceptance criteria**

-   WSL session: inserted paths are POSIX-style

-   Non-WSL session: unchanged from M0/M1 behavior

* * * * *

M2.5 --- Tests
------------

### Unit tests

-   Mapper caching behavior (mock runner).

-   Win path normalization edge cases.

-   DropRouter uses mapper when `IsWslSession == true`.

### Integration tests (Windows-only if your CI supports it)

-   Actually run `wsl.exe wslpath` and assert mapping.

-   If CI can't run WSL, keep this test optional/guarded.

* * * * *

Suggested code structure
========================

### Core

-   `src/NovaTerminal.Core/Paths/IPathMapper.cs`

-   `src/NovaTerminal.Core/Paths/IdentityPathMapper.cs`

-   `src/NovaTerminal.Core/Paths/WslPathMapper.cs`

-   `src/NovaTerminal.Core/Paths/WslPathMapperCache.cs` (or internal LRU)

-   `src/NovaTerminal.Core/Process/IProcessRunner.cs` (abstract spawning)

-   `src/NovaTerminal.Core/Sessions/SessionContext.cs` (add `WslDistroName?`)

### DropRouter change

-   add `IPathMapper` dependency and call it when WSL.