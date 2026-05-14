# Native SFTP Sidebar Design

## Goal

Improve NovaTerminal's Native SFTP transfer UX by replacing the disconnected right-click-plus-modal flow with a pane-local, lightly navigable remote sidebar that stays terminal-first and does not become a general file browser.

## Scope

This design adds:

1. A pane-local Native SFTP sidebar
2. Lightweight remote directory navigation
3. Transfer actions grounded in visible remote context
4. CWD-aware defaulting for remote operations

This design does not add:

- VT parsing or rendering changes
- transfer UI inside terminal grid content
- an OpenSSH remote browser
- recursive tree browsing
- file preview, rename, delete, or other file-manager behaviors

## Constraints

- Use Avalonia for all new UI.
- Keep SFTP UX separate from terminal core logic.
- Prefer additive changes over refactors.
- Preserve current `SftpService` ownership of transfer queueing and progress.
- Auto-hide the sidebar in alternate-screen/fullscreen TUI mode.

## Problem Summary

The current SFTP UX feels disconnected for two reasons:

1. Transfer initiation starts from a generic pane context menu rather than from a visible remote context.
2. The transfer dialog defaults remote input to `profile.DefaultRemoteDir ?? "~"` rather than the pane's active remote working directory.

That leaves upload and download feeling like detached utility actions instead of part of the SSH pane workflow.

## Chosen Direction

Use a pane-local right sidebar on Native SSH panes as the primary transfer-entry surface.

The sidebar should:

- open from the active pane
- show the current remote path
- list one remote directory at a time
- allow lightweight navigation
- let upload target the displayed directory
- let download act on the selected remote file or folder

The existing transfer dialog can remain as a fallback/manual path surface, but it should stop being the primary Native SSH flow.

## Alternatives Considered

### 1. Pane-Local Slide-Out Sidebar

Recommended.

Pros:

- feels connected to the active SSH pane
- keeps remote context visible while preserving terminal visibility
- makes upload and download target selection much clearer
- fits existing pane-local overlay patterns

Cons:

- broader scope than a dialog-only fix
- needs careful limits to avoid drifting into file-manager territory

### 2. Persistent Docked Remote Sidebar

Rejected for v1.

Pros:

- very discoverable
- always visible

Cons:

- permanently consumes pane width
- too expensive for small panes
- competes with terminal-first usage

### 3. Larger Popup Browser

Rejected as the primary surface.

Pros:

- cheaper than a docked rail
- can still provide remote context

Cons:

- still feels transient and utility-like
- does not fix the "part of the pane" feel as well as a sidebar

## Product Direction

NovaTerminal should remain terminal-first, not file-manager-first.

That means the sidebar must stay intentionally narrow:

- one directory at a time
- no tree
- no preview
- no mutation actions beyond starting transfers
- keyboard-friendly, minimal chrome

The target feel is "remote transfer helper attached to a pane", not "embedded SFTP client."

## Sidebar Surface

### Placement

- Right side of the active `TerminalPane`
- Pane-owned, not `MainWindow`-global
- Visible only for Native SSH panes with an active session

### Sections

- Header: remote path, back, refresh, close
- Body: current directory listing
- Footer: `Upload File`, `Upload Folder`, `Download Selected`

### Navigation

Supported:

- enter folder
- go up one level
- go back through visited directories
- refresh current directory

Not supported:

- recursive tree expansion
- multi-select
- drag-and-drop between local and remote surfaces

## Session And Path Rules

### Availability

The sidebar is available only when:

- the pane is an SSH pane
- the backend is `SshBackendKind.Native`
- there is an active session for that pane

### Initial Path

When opening the sidebar, resolve the starting path in this order:

1. pane's best-known remote current working directory
2. `profile.DefaultRemoteDir`
3. `~`

### Shell CWD Changes

If the shell current directory changes while the sidebar is open:

- do not forcibly re-navigate the sidebar
- show a small `Jump To Current Directory` affordance instead

This keeps the user's browsing context stable while still acknowledging live shell context.

## Directory Listing Behavior

- One directory is listed per query.
- Directories sort before files.
- Names sort alphabetically within their rank group.
- Selection is single-item only.
- Files are selectable and downloadable.
- Directories are selectable, downloadable, and navigable.

Keyboard behavior:

- `Up` / `Down` changes selection
- `Enter` opens a selected directory
- `Backspace` navigates up one level when the list has focus
- `Alt+Left` navigates back
- `Escape` closes the sidebar

Double-click on a directory should also navigate into it.

## Transfer Behavior

### Upload

- `Upload File` picks a local file and uploads it into the currently displayed remote directory.
- `Upload Folder` picks a local folder and uploads it into the currently displayed remote directory.
- The sidebar removes the need to type a remote destination for common uploads.

### Download

- `Download Selected` is enabled only when a remote file or folder is selected.
- Download acts on the selected entry.
- Local destination selection can still use the existing local picker flow.

### Execution Boundary

The sidebar should not own transfer execution. It only produces clearer transfer requests. `SftpService` remains the execution and progress boundary.

## Failure And State Handling

- No active session: entry point hidden or disabled.
- Directory listing failure: show inline error and `Retry`, no modal.
- Missing path or permission error: show inline error, keep navigation controls available.
- Session disconnect while open: freeze list actions, show disconnected state, allow close.
- Transfer start failure: continue to surface through existing transfer job error handling.
- Long-running transfer progress remains in the existing Transfer Center / status UI rather than moving into the sidebar.

## Implementation Shape

Preferred split:

- `TerminalPane`: owns sidebar visibility, placement, keyboard routing, alt-screen hiding, and pane/session context
- sidebar view model/controller: owns path, entries, selection, loading/error state, and back-stack
- remote directory browser service: owns native directory listing calls and navigation/path normalization
- `SftpService`: continues to own job queueing, progress, cancellation, and result state

This keeps UI concerns out of terminal core logic and keeps the SFTP helper additive.

## Testing Strategy

### Deterministic unit tests

- initial-path resolution
- directories-first sorting
- back-stack behavior
- jump-to-current-directory state changes
- disabled download with no selection
- inline listing error state transitions

### Pane/UI tests

- sidebar opens only for eligible Native SSH panes
- sidebar hides in alternate-screen mode
- keyboard navigation updates selection and path correctly
- directory double-click / `Enter` opens child folder

### Existing transfer tests remain

- `SftpService` tests keep execution coverage
- native SFTP interop and Docker E2E tests remain the backend confidence layer

Avoid snapshot-heavy UI tests.

## Rollout Order

1. add remote directory browser state/service
2. add pane-local sidebar control and host integration
3. wire upload/download actions through the sidebar
4. add alt-screen and disconnect behavior
5. keep manual dialog flow as fallback where still useful

## Expected Outcome

After this work:

- Native SFTP actions feel attached to the active SSH pane
- remote defaults come from live session context instead of `~`
- downloads can start from visible remote entries instead of typed paths
- uploads target the currently shown remote directory
- NovaTerminal remains terminal-first without becoming a full SFTP browser
