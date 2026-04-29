# Native SFTP Progress Design

**Date:** 2026-04-27

**Goal:** Add real live transfer progress for NativeSSH file and folder transfers without changing the OpenSSH `scp` path.

## Scope

- NativeSSH transfers only
- Byte-based progress when total size is known
- Indeterminate progress when total size is unknown
- Reuse the existing `TransferJob` UI path in `SftpService`, `TransferCenter`, and `TerminalPane`
- No redesign of the OpenSSH transfer path in this change

## Current Problem

The NativeSSH transfer path now uses built-in SFTP, but the Rust interop is still a one-shot call. C# waits until the native transfer returns, so there is no reliable stream of byte progress into the UI. The recent UI fixes made job state visible, but they cannot show true live byte progress until the native backend emits progress during the copy loop.

## Chosen Approach

Add a Rust-to-C# progress callback to the existing native transfer call.

Why this approach:

- Smallest change that unlocks real progress
- Keeps transfers separate from terminal session events
- Preserves the current `SftpService` ownership of queueing, job state, and UI notifications
- Avoids inventing a larger start/poll/finish transfer handle API

## Rejected Approaches

### 1. Polling transfer handles

This would create a cleaner long-term transfer API, but it is too much surface area for this problem. It would force a broader redesign of the native interop boundary and cancellation flow.

### 2. Reusing native terminal session events

This mixes transfer concerns into the terminal-session event channel. The current architecture intentionally keeps transfers separate from terminal rendering and PTY flow. Reusing session events would weaken that boundary.

### 3. Synthetic estimated progress

This would show movement even when totals are unknown, but it would be misleading. The chosen behavior is determinate only when the backend can provide a real byte total.

## Architecture

### Native interop boundary

Extend the `nova_ssh_sftp_transfer` ABI so C# can provide a callback function pointer plus opaque user data. Rust will invoke the callback with:

- `bytes_done`
- `bytes_total`
- `current_path`

The callback is advisory only. It must not own transfer state or UI logic.

### Rust transfer behavior

The Rust SFTP layer will:

- determine file size when available before starting a file copy
- emit progress after each chunk written
- emit per-file progress for directory transfers using the current file path
- preserve cancellation checks during the same copy loops

For directory transfers, the first version will report progress for the current file, not a precomputed recursive total for the entire tree. That keeps the implementation honest and small.

### C# transfer behavior

`NativeSshInterop` will:

- marshal the callback into managed code
- translate native progress payloads into `NativeSftpTransferProgress`
- invoke the existing `progress` delegate already accepted by `RunSftpTransfer`

`SftpService` already knows how to apply native progress to `TransferJob`. It will continue to own job-level updates and UI notifications.

### UI behavior

No new transfer surface is required for this step. Existing UI should improve automatically once native progress starts flowing:

- `TransferCenter` shows determinate progress when `BytesTotal > 0`
- `TransferCenter` shows indeterminate progress when total is unknown
- `TerminalPane` status text can show meaningful percentages when available

## Error Handling

- Progress callback failures in managed code must not crash the transfer process
- If a native path cannot determine size, the transfer continues with `BytesTotal = 0`
- Existing cancellation and transfer error mapping stay in place

## Testing Strategy

### C# tests

- interop unit test that proves a native progress callback is translated into `NativeSftpTransferProgress`
- `SftpService` test that proves job progress/state properties update from native callback payloads

### Rust/native tests

- targeted unit test for progress payload serialization and callback invocation where practical
- no change to VT, terminal rendering, or unrelated SSH session behavior

### End-to-end tests

- Docker NativeSSH file download/upload tests should observe progress updates before completion
- directory transfer tests should observe current-file progress callbacks without requiring recursive byte pre-summing

## Success Criteria

- NativeSSH file transfers visibly update progress while running
- NativeSSH folder transfers visibly update the active file progress while running
- Unknown total size falls back to indeterminate progress
- OpenSSH transfer behavior remains unchanged
