# Native SFTP Sidebar V3 Design

## Goal

Polish NovaTerminal's Native SSH remote-files sidebar into a denser, more legible pane-local utility rail while preserving its terminal-first scope.

## Product Direction

The sidebar should stay a remote transfer helper attached to a terminal pane, not evolve into an embedded file manager.

The design target is a narrowed version of the provided mockup:

- keep the compact right rail
- keep strong host identity and current path context
- keep a denser remote listing
- keep contextual upload and download actions
- do not add a second transfer-progress surface
- do not add file mutation workflows
- do not add multi-select or file-manager behaviors

## Scope

This pass adds:

1. a refined compact sidebar presentation
2. a dense two-column remote listing
3. a secondary `Modified` column
4. stronger visual identity for the active remote host

This pass does not add:

- VT parsing or rendering changes
- transfer UI inside terminal grid content
- an embedded transfer queue
- drag-and-drop upload surfaces
- recursive tree browsing
- remote search across directories
- multi-select
- rename, edit, delete, chmod, or preview actions

## Constraints

- Use Avalonia for all new UI.
- Keep SFTP UX separate from terminal core logic.
- Keep `SftpService` as the transfer execution and progress boundary.
- Keep the sidebar pane-local inside `TerminalPane`.
- Auto-hide the sidebar in alternate-screen/fullscreen TUI mode.
- Prefer additive changes over broad refactors.

## Chosen Direction

Use a compact right rail with a dense two-column list:

- primary column: name-first entry label with stronger directory emphasis
- secondary column: modified time only

This keeps the rail compact enough for terminal use while adding the single most useful secondary signal for SSH workflows such as log inspection, config browsing, and runtime validation.

`Modified` is a better first secondary column than `Size` because it supports the more terminal-native question of "what changed recently?" without pulling the UI toward a download manager. `Size` can remain a follow-up if later product work shows a strong need.

## Sidebar Shape

### Layout

- Header row:
  - host label
  - connection subtitle
  - compact utility actions
  - close action
- Path row:
  - current remote path with truncation
  - refresh
  - item count
- Body:
  - dense two-column listing
  - `Name`
  - `Modified`
- Footer:
  - `Upload File`
  - `Upload Folder`
  - `Download Selected`

### Visual Intent

The target feel is "small attached utility rail", not "mini file manager."

That means:

- tighter row height
- clearer host context
- stronger column rhythm
- minimal chrome
- no bottom transfer dashboard

## Command Surface

The command split should remain:

- pane context menu: open `Remote Files`
- sidebar: contextual transfers grounded in visible remote context
- command palette: manual typed-path transfer flow

The pane context menu should not reintroduce generic upload/download actions beside the sidebar entry.

## Directory Listing Model

The current entry contract is too small for the desired list presentation. The sidebar list should expand from name/path/directory metadata to include modified-time metadata.

Implemented model shape:

- `Name`
- `FullPath`
- `IsDirectory`
- `ModifiedAtUtc` or equivalent raw timestamp when available

Optional later follow-up:

- `SizeBytes` for non-directory entries

Recommendation:

Store raw metadata in the app-layer model and format it in the view model. Do not bake display strings into the service contract unless backend constraints make that unavoidable.

If modified metadata is unavailable for an entry, the sidebar should still render the listing and show a simple placeholder such as `-`.

## Behavior

Behavior stays intentionally narrow:

- one directory listing at a time
- single selection only
- directory-first sorting, then alphabetical
- `Enter` navigates into a selected directory
- `Download Selected` operates on the current selected entry
- uploads still target the currently displayed directory
- downloads still use the picker-first flow already established in V2

No embedded transfer queue should appear in the sidebar. Long-running transfer state remains in the existing Transfer Center and status surfaces.

## Inline Upload Surface

Do not add a drag-and-drop upload panel in this pass.

Keep upload initiation compact:

- `Upload File`
- `Upload Folder`

That keeps the rail small, reduces visual weight, and avoids pulling the design toward a browser-like transfer workspace.

## Architecture

The ownership split remains:

- `TerminalPane`
  - owns sidebar hosting, visibility, and alt-screen hiding
- remote directory browser service
  - fetches one directory listing at a time
  - expands to include modified metadata
- sidebar view model
  - owns navigation, loading, selection, and list presentation state
- `MainWindow`
  - owns local picker invocation and transfer job creation
- `SftpService`
  - remains the execution and progress boundary

This should be treated as a sidebar polish pass, not as a remote-files feature expansion.

## Testing

Update coverage in four areas:

- remote directory browser tests for modified metadata mapping
- sidebar view model tests for metadata handling and placeholder behavior
- headless Avalonia sidebar tests for the dense two-column layout
- existing transfer-flow and alt-screen/disconnect regression tests to confirm no behavioral regressions

## Expected Outcome

After this pass:

- the sidebar looks materially closer to a polished NovaTerminal surface
- the rail remains compact and pane-local
- remote context is clearer
- recently modified entries are visible without opening a second details surface
- users can quickly see what changed recently in the current directory
- the UX remains aligned with NovaTerminal's terminal-first philosophy
- the product does not drift into an embedded SFTP client or file manager
