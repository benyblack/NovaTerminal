# Transfer UI Design

## Goal

Improve NovaTerminal's transfer UX without turning the app into a file browser. Keep the terminal-first model, keep the backend transfer work intact, and replace the current prompt-heavy transfer flow with a focused dialog and a more usable transfer manager surface.

## Scope

This design covers three UI areas:

1. Transfer initiation
2. Transfer Center behavior
3. Transfer history management

It does not change VT rendering, terminal parsing, or the native SFTP backend contract beyond small UI-facing metadata that may already exist.

## Constraints

- Use Avalonia for all new UI.
- Keep UI concerns out of terminal core logic.
- Prefer additive changes over refactors.
- Keep the backend transfer execution path stable.
- Avoid turning transfers into a full remote file explorer in this pass.

## Problem Summary

The current flow has three major problems:

1. Starting a transfer is awkward:
   - nested context menus
   - a generic path prompt with little guidance
   - weak keyboard focus behavior
   - poor discoverability

2. The Transfer Center is too static:
   - fixed floating position
   - no history cleanup affordances
   - limited row actions

3. Transfer state feedback is not good enough:
   - weak confirmation of start
   - stale finished/failed rows accumulate
   - errors are visible but not presented cleanly

## Chosen Direction

Use a dedicated Transfer Dialog as the primary transfer-entry surface, and keep the Transfer Center as a lightweight movable floating manager for active and recent jobs.

This is more aligned with NovaTerminal than a remote file browser because:

- NovaTerminal is terminal-first, not file-manager-first
- there is no existing remote browsing pattern to extend
- the immediate UX problem is workflow and focus, not missing directory navigation
- a file browser would expand scope into remote listing, navigation, loading, and permissions states

## Alternatives Considered

### 1. Dedicated Transfer Dialog

Recommended.

Pros:
- clear and explicit
- fixes focus and paste behavior directly
- works for upload/download and file/folder without branching UI
- minimal backend impact

Cons:
- still requires users to type remote paths
- less discoverable than a browser for unknown server layouts

### 2. Remote File Browser

Rejected for this pass.

Pros:
- stronger discoverability
- easier path selection when users do not know remote paths

Cons:
- much larger scope
- pushes NovaTerminal toward a mini SFTP client
- requires directory listing, navigation, refresh, and permissions UX

### 3. Command Palette Only / Quick Actions

Rejected as the primary flow.

Pros:
- fast for advanced users
- low UI footprint

Cons:
- still weak for collecting both local and remote transfer inputs
- does not solve the need for a proper form surface

## Transfer Dialog

### Purpose

Provide a single coherent surface for creating upload/download jobs.

### Behavior

- Open directly from transfer actions.
- Preselect the requested mode:
  - Upload File
  - Upload Folder
  - Download File
  - Download Folder
- Show both sides of the transfer in one place:
  - local path
  - remote path
- Keep keyboard focus inside the dialog:
  - `Ctrl+V` pastes into the focused field
  - `Enter` confirms when valid
  - `Escape` cancels

### Fields

- Transfer direction selector
- Transfer kind selector
- Local path input with browse button
- Remote path input with examples / placeholders

### Defaults

- Use the selected transfer action to prefill direction and kind.
- Prefill remote path from `profile.DefaultRemoteDir ?? "~"`.
- For download-file actions, when possible, use the remote leaf name as the suggested local filename once a remote path is chosen.

### Validation

- local path required
- remote path required
- file/folder mode must match the selected local picker path where practical
- validation errors shown inline in the dialog instead of surfacing only after queueing

### Entry Points

- terminal context menu actions remain
- command palette actions remain
- both launch the same dialog

## Transfer Center

### Purpose

Act as a lightweight transfer manager for active and recent jobs.

### Behavior

- Remain a floating overlay/tool surface
- Open automatically when a new transfer starts
- Avoid stealing focus aggressively
- Become movable by dragging its title bar

### Why Floating Instead Of Docked

- transfers are secondary workflow state
- a permanent docked panel would compete with terminal space
- a modal would interrupt long-lived terminal usage too much

### Row Presentation

Each row should clearly show:

- transfer name
- running/completed/failed/canceled state
- remote path
- local path
- progress bar when applicable
- compact error text for failures

### Row Actions

- Running: `Cancel`
- Finished / Failed / Canceled: removable via row action or bulk clear

## History Management

Add explicit cleanup affordances:

- `Clear Finished`
- `Clear Failed`
- `Clear All Inactive`

Rules:

- running jobs cannot be cleared
- canceled jobs are treated as inactive
- clearing is UI-only history cleanup, not backend job mutation

## Focus And Input Rules

The dialog must own text input while open.

Expected behavior:

- typing edits the focused field
- paste goes into the field, not the terminal
- overlay dismissal should not leak keystrokes into the terminal session

This replaces the current generic `PathPromptOverlay` behavior for transfer setup.

## Implementation Shape

- Add a dedicated Avalonia transfer dialog/window
- Replace `PromptForRemotePathAsync` for transfer flows
- Keep `SftpService` as the transfer execution/service layer
- Add only small UI-supporting service/model changes
- Upgrade `TransferCenter` into a draggable overlay with clear-history actions

## Testing Strategy

- deterministic service tests for history clearing and remote-path normalization if touched
- headless Avalonia tests for dialog validation and transfer-center interactions where practical
- avoid broad snapshot-based UI testing

## Rollout Order

1. Transfer Dialog
2. Transfer Center movement + cleanup actions
3. Removal of the old path-prompt transfer path

## Expected Outcome

After this work:

- starting a transfer is a single, clear, keyboard-safe flow
- transfer history is manageable
- the Transfer Center behaves like a small tool window rather than a fixed overlay
- backend NativeSSH/OpenSSH transfer behavior stays intact while the UI becomes materially easier to use
