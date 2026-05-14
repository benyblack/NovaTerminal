# Native SFTP Sidebar V2 Design

## Goal

Refine the Native SSH remote-files UX so the sidebar is more compact and sidebar-initiated transfers no longer bounce through the generic transfer dialog.

## Product Direction

The sidebar should be the primary contextual transfer surface for Native SSH panes.

The command palette should remain the manual or advanced path-based transfer surface.

The pane right-click menu should stop exposing the old upload and download commands. Its only SFTP-related job should be opening `Remote Files`.

This gives each surface one clear purpose:

- pane context menu: open the remote files surface
- remote files sidebar: everyday contextual transfers
- command palette: manual typed-path transfer flow

## UX Changes

### 1. Compact the sidebar

The current sidebar is heavier than necessary. It reads like a panel with several stacked control bands rather than a compact utility rail.

V2 should tighten it to:

- width around `272-288`
- one compact toolbar row with `Back`, `Up`, path text, `Refresh`, `Close`
- `Jump to Current Directory` shown only as a small conditional chip
- denser listing rows
- no always-visible full path under each row
- a lighter footer with only primary transfer actions

The visual goal is "small attached utility rail", not "mini file manager."

### 2. Make sidebar transfers picker-only

Sidebar actions should stop opening `TransferDialog`.

The sidebar already provides the remote-side context, so only the local-side choice still needs UI.

Sidebar upload flow:

- `Upload File` -> local file picker -> upload into sidebar `CurrentPath`
- `Upload Folder` -> local folder picker -> upload into sidebar `CurrentPath`

Sidebar download flow:

- selected remote file -> `SaveFilePicker` with filename prefilled
- selected remote folder -> local folder picker

After the picker returns a local target, `MainWindow` should enqueue the transfer directly through `SftpService`.

### 3. Keep the transfer dialog for manual flows only

The existing transfer dialog still has value for command-palette or explicit manual SFTP actions where the user wants to type or edit both sides.

That means:

- pane/sidebar contextual flow: no transfer dialog
- command palette/manual flow: keep transfer dialog

This keeps the advanced path available without forcing it into the common case.

## Architecture

The ownership split should stay the same:

- `TerminalPane` owns the compact sidebar surface and raises explicit sidebar-originated transfer requests
- `MainWindow` owns local picker invocation and transfer job creation
- `SftpService` remains the execution boundary
- `TransferDialog` remains in place only for manual command flows

The main architectural change is removing sidebar dependence on `TransferDialogRequest.ForSidebarAction(...)` and replacing it with direct picker-based transfer helpers in `MainWindow`.

## Command Surface

For Native SSH panes:

- remove `Upload File`, `Upload Folder`, `Download File`, `Download Folder` from the pane context menu
- keep only `Remote Files` there

For the command palette:

- keep manual `SFTP: Upload File...`, `SFTP: Upload Folder...`, `SFTP: Download File...`, `SFTP: Download Folder...`

This removes the current split-brain UX where the same pane exposes both the new contextual model and the old generic dialog model side by side.

## Error Handling

Sidebar transfer actions should stay quiet and direct:

- if the user cancels a local picker, do nothing
- if no remote selection exists for download, action remains disabled
- if the SSH session is disconnected, sidebar actions remain disabled
- transfer start failures still surface through the existing transfer error path

The sidebar should not become a second transfer-progress surface.

## Testing

Update coverage to reflect the new split:

- sidebar upload tests should assert local file or folder picker usage plus `CurrentPath` targeting
- sidebar file download tests should assert `SaveFilePicker` usage with the remote filename
- sidebar folder download tests should assert local folder picker usage
- manual transfer tests should continue asserting `TransferDialog` usage
- pane menu tests should assert that only `Remote Files` remains in the Native SSH context menu

The existing disconnect and alt-screen behavior stays part of the sidebar regression surface.

## Expected Outcome

After this pass:

- the sidebar feels lighter and more purposeful
- contextual transfers no longer ask the user to confirm the already-known remote side
- manual typed-path transfers still exist, but only where users expect them
- the right-click menu becomes simpler and less duplicative
