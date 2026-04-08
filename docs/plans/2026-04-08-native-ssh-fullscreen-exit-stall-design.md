# Native SSH Fullscreen Exit Stall Design

**Date:** 2026-04-08

## Goal

Eliminate the native SSH stall/flicker that can happen when a fullscreen TUI such as `mc` exits after aggressive resizing, without changing VT parsing or general rendering behavior.

## Scope

- Native SSH backend only
- Resize handling between `TerminalView`, `NativeSshSession`, and the Rust `rusty_ssh` worker
- Fullscreen/alternate-screen exit scenarios where resize storms overlap with remote redraw
- Deterministic regression coverage for resize coalescing

## Non-Goals

- No OpenSSH behavior changes
- No terminal parser/rendering refactor
- No generic UI resize throttling changes for all terminal backends
- No automatic reconnect or session lifecycle redesign
- No Command Assist behavior changes unless a later investigation proves they are directly involved

## Existing Context

- The issue reproduces with native SSH, not OpenSSH.
- It is easier to trigger with fullscreen TUIs such as `mc`, aggressive pane resizing, and then exiting the app.
- The pane can appear stuck or flicker; another resize often causes recovery.
- `TerminalView` already throttles resize events to roughly one PTY resize dispatch every 60 ms.
- The native backend currently forwards every dispatched resize to `NativeSshSession.Resize(...)`, which immediately calls native interop.
- The Rust worker processes commands sequentially; each resize becomes a separate `window-change` request on the SSH channel.

## Problem

Under heavy resize churn, the native backend can accumulate a backlog of stale `window-change` commands. When the fullscreen app exits and the remote shell redraws, those redraw-triggering resizes may still be queued and processed one by one. That can delay or destabilize the post-exit redraw path, which fits the observed pattern:

- native only
- easier to trigger with aggressive resize
- often recovers on another resize

This points to resize-command backlog rather than a parser bug or a pure repaint omission.

## Recommended Approach

Coalesce resize commands in the native backend so only the most recent dimensions survive backlog.

### C# side

- Keep `TerminalView` throttling unchanged.
- Keep `NativeSshSession.Resize(...)` simple and backend-specific.
- If needed, add a small native-session-side optimization later, but do not lead with extra state here.

### Rust side

- Change the worker command handling so a pending resize does not enqueue an unbounded sequence of stale `window-change` requests.
- When the worker receives a resize command, drain any immediately pending resize commands from the command channel and retain only the latest `(cols, rows)` pair.
- Apply one `channel.window_change(...)` call using the final coalesced dimensions.

This targets the native-only bottleneck directly, preserves current UI behavior, and avoids broad side effects.

## Why This Approach

### Compared with stronger global resize throttling

Increasing or debouncing `TerminalView` resize dispatch would affect all local and SSH sessions and make resize feel less responsive. It treats the symptom at the UI edge, not the native SSH command queue.

### Compared with forcing extra redraws on alt-screen exit

The current alt-screen path already triggers invalidation and screen-switch notifications. Extra redraw forcing may mask the issue, but it does not explain the native-only behavior nearly as well as resize backlog.

## Architecture Notes

- Keep the fix backend-specific to native SSH.
- Preserve the current terminal-core and renderer behavior.
- Keep the Rust worker as the authoritative place where sequential SSH commands are serialized.
- Prefer coalescing rather than adding cross-layer complexity.

## Testing Strategy

Add deterministic tests that prove:

- native session resize forwarding still works with the latest dimensions
- the Rust worker applies only the last resize in a burst of back-to-back resize commands
- no regression to ordinary native SSH output/exit behavior

Manual verification should cover:

- native SSH + `mc`
- aggressive pane resize while `mc` is open
- exit from `mc`
- shell prompt redraw remains responsive without requiring another resize

## Risks

### Hidden Ordering Dependency

If some other command must happen between two resizes, overly aggressive draining could accidentally reorder behavior. The implementation should coalesce only consecutive pending resize commands, not unrelated commands.

### False Root Cause

If resize coalescing alone does not resolve the issue, the next investigation target should be native-only post-alt-screen redraw timing, not a broad VT refactor.

### Test Coverage Gap

The exact runtime symptom is timing-sensitive, so the automated tests should assert queue/coalescing semantics rather than attempting flaky UI reproduction.
