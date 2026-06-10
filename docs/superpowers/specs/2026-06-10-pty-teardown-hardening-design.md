# PTY Teardown Hardening — Design

**Date:** 2026-06-10
**Issues:** Closes #119, #118, #103; targeted part of #102. Follow-up to #81 (Mode A).
**Branch (planned):** `fix/issue-81-teardown`

## Background

#81 ("Unit Tests" testhost teardown hang) was root-caused into two independent
modes. **Mode A** (in-repo PTY/pane leaks → threadpool starvation) was contained
on the *test* side in #89 by moving the PTY loops to dedicated `IsBackground`
threads and disposing test panes. The underlying *production* defects that Mode A
exposed remain open:

- **#119** — a blocking native `pty_read` has no cancellation path; `pty_close`
  cannot unblock an in-flight read, so closing a session can leave the C# read
  thread stuck in native code until the child next produces output or exits
  (slow-to-close sessions).
- **#118** — use-after-free window: `pty_close` consumes the handle's `Arc` via
  `Arc::from_raw` and drops it, while every other export reconstitutes the same
  raw pointer. A `pty_read` (etc.) that races or follows `pty_close` does
  `Arc::from_raw` on freed memory (UB). The C# `_ptyState != IntPtr.Zero` guard is
  an unsynchronized TOCTOU.
- **#103** — `TerminalPane.Dispose` does not enforce teardown ordering (stop
  session → drain output → detach parser → release buffer) and is not idempotent
  against in-flight output.
- **#102** (targeted subset) — timers and a few cross-lifetime/restart event
  subscriptions are never cleaned up.

These are not separable: you cannot safely `pty_close` until the read thread has
been joined, and you cannot join the read thread until the native read has been
unblocked. #119 + #118 + the native side of #103 therefore collapse into a single
teardown contract.

## Goals / Non-goals

**Goals**
- A native `pty_cancel_read` that promptly unblocks an in-flight read.
- Eliminate the #118 UAF by construction (not by hope-ordering).
- Ordered, idempotent `RustPtySession.Dispose` and `TerminalPane.Dispose`.
- Stop the leaking timers (`_metricsTimer`, `_recordingToastTimer`) and the
  restart-path parser-handler accumulation (duplicate bell/title symptom).

**Non-goals**
- Re-gating the headless `App.Tests` lane — that is **Mode B**, upstream
  (AvaloniaUI/Avalonia#21467), tracked in #117. Untouched here.
- Mirroring `SafeHandle` to `rusty_ssh` handles (#118 notes the same pattern
  exists there). **Recommended follow-up**, deliberately out of this PR's scope —
  it widens the diff and the SSH read path differs.
- A full `CompositeDisposable` refactor of every `+=` site. We fix only what
  crosses lifetimes or accumulates; purely intra-pane one-shot lambdas GC with the
  pane and are left alone.

## Approach decisions (locked)

- **Native cancel:** kill-to-EOF (Approach 1) — `pty_cancel_read` breaks the pty
  so the in-flight `read()` returns, using mutexes the blocked read does *not*
  hold.
- **UAF safety:** `PtySafeHandle` (Approach 3) — runtime ref-counting makes
  `pty_close` impossible during any in-flight `pty_*` call.
- **#102 depth:** targeted (timers + cross-lifetime/restart subscriptions only).

## Components & files

| File | Change |
|---|---|
| `src/NovaTerminal.App/native/src/lib.rs` | New `pty_cancel_read` export; move `h_pc`/`h_process` behind `Mutex<Option<…>>` so cancel and close don't double-free |
| `src/NovaTerminal.Pty/RustPtySession.cs` | `PtySafeHandle : SafeHandle`; all `Native.*` signatures take the handle; idempotent ordered `Dispose` |
| `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs` | Idempotent `Dispose` guard; 9 `Parser.On*` lambdas → named handlers detached on restart + `Dispose` |
| `src/NovaTerminal.App/Shell/TerminalView.cs` | Stop `_metricsTimer` on detach |
| `src/NovaTerminal.App/MainWindow.axaml.cs` | Stop `_recordingToastTimer` in `OnClosing` |

## B. Native cancel + `PtySafeHandle`

### `pty_cancel_read(state)`
Unblocks the read that is parked inside `reader.read()` while holding the
`state.reader` mutex. The cancel path therefore must **never** touch `reader`.

- **Portable path (Unix + Windows non-passthrough):** lock `state.child`, call
  `child.kill()`. Child exit closes the slave → master read returns EOF.
- **Windows passthrough path:** close the `HPCON` and set it to `None`. Closing
  the pseudoconsole breaks the output pipe → `read()` on `h_out_read` returns.
  Because `pty_close` also closes the `HPCON`, `h_pc`/`h_process` move from plain
  `Option<…>` to `Mutex<Option<…>>` and both functions `take()` so neither
  double-frees. `pty_close` already guards on the `Option`.
- Wrapped in `ffi_guard`; null-safe; idempotent (second call is a no-op once
  handles are `None` / child already killed).

### `PtySafeHandle : SafeHandle`
- `IsInvalid => handle == IntPtr.Zero`.
- `ReleaseHandle()` → `pty_close(handle)`; returns `true`.
- `pty_spawn` / `pty_spawn_with_envs` return `PtySafeHandle` (marshaler
  constructs it from the returned pointer).
- Every other `Native.*` P/Invoke takes `PtySafeHandle` instead of `IntPtr`. The
  runtime auto-`DangerousAddRef`s before the call and `Release`s after, so
  `ReleaseHandle` (`pty_close`) cannot run while any `pty_*` call — including a
  blocked `pty_read` — is in flight. **This is what eliminates #118.**

> Note: with `SafeHandle`, `pty_cancel_read` is still required. Without it, the
> handle ref stays held by the blocked read and `ReleaseHandle` is deferred until
> the child happens to output/exit — i.e. the slow-close of #119 persists. Cancel
> makes the read return promptly so the ref releases and close can proceed.

## C. `RustPtySession.Dispose` — the teardown contract

Idempotent via an `Interlocked` guard (`_disposed`). Safe to call off the UI
thread (it is invoked through `Task.Run` in `DisposeControlTree`).

```
Dispose():
  1. if Interlocked.Exchange(ref _disposed, 1) != 0 → return
  2. StopRecording() if recording
  3. _cts.Cancel()                         // read loop won't re-enter pty_read
  4. Native.pty_cancel_read(_handle)       // unblock in-flight native read
  5. _outputQueue.CompleteAdding()         // process loop drains + exits
  6. _readLoopThread.Join(JoinTimeout)     // bounded (~2s); log on timeout
     _processLoopThread.Join(JoinTimeout)
  7. _handle.Dispose()                     // SafeHandle → ReleaseHandle → pty_close
  8. TryNotifyExit(0)
```

Join is bounded so `Dispose` never hangs; `SafeHandle` keeps the close safe even
if a join times out, and the `IsBackground` thread cannot block process exit.

## D. `TerminalPane` (#103 pane side + targeted #102)

- **Idempotent** `Dispose` via a guard flag.
- **Order:** stop/null `_statusTimer` → detach `Parser.On*` handlers → unsubscribe
  `SftpService.JobUpdated` + `Buffer.OnScreenSwitched` (already present) →
  `UnregisterActiveSshSession` (already present) → `Session.Dispose()` (runs the
  contract in C, draining output) → drop references.
- **Parser handlers:** convert the 9 `Parser.On*` lambdas (`OnBell`,
  `OnTitleChanged`, `OnWorkingDirectoryChanged`, `OnPromptReady`,
  `OnCommandAccepted`, `OnCommandStarted`, `OnCommandFinished`,
  `OnCommandFinishedDetailed`, `OnResponse`) to **named methods** stored once.
  Detach them both in `Dispose` and on the **session-restart path** that
  re-creates the `Parser`, fixing the documented duplicate bell/title bug.
- **Left as-is:** purely intra-pane one-shot lambdas (context-menu items, search
  box, toast buttons) — they share the pane's lifetime and GC together.

## E. `TerminalView` + `MainWindow` (targeted #102)

- **`TerminalView`:** `_metricsTimer` is started in the ctor but never stopped by
  `StopUiTimers`; `OnDetachedFromVisualTree` calls `StopUiTimers`. Stop
  `_metricsTimer` on detach so it stops rooting the view via its `Tick` delegate.
- **`MainWindow`:** stop `_recordingToastTimer` in `OnClosing`.

## F. Testing strategy (TDD)

All in the **non-headless** lanes (`NovaTerminal.Pty` / lifecycle), unaffected by
the Mode-B headless flake.

- **Rust unit tests** (`lib.rs`):
  - spawn a long-running child (no output), call `pty_cancel_read`, assert the
    next `pty_read` returns within a timeout (EOF/error).
  - `pty_close` after `pty_cancel_read` is safe; double `pty_close` is safe.
- **C# `PtyThreadLifecycleTests` extensions:**
  - start a session whose child produces no output, `Dispose`, assert both loop
    threads exit within `JoinTimeout`.
  - `Dispose` is idempotent (call twice; no throw, no double-close).
  - no `OnOutputReceived` / `OnExit` fires after `Dispose` completes.
  - threads remain `IsBackground`, non-threadpool (existing guard).

## G. Risks to validate during implementation

- **ConPTY EOF semantics:** whether killing the child alone breaks the
  passthrough read or `ClosePseudoConsole` is required. The design routes
  passthrough-cancel through closing the `HPCON`; the Rust test in (F) confirms
  the read actually returns.
- **`Dispose` off the UI thread:** it runs via `Task.Run`. Confirm
  `CloseRemoteFilesSidebar` and any other step touch no UI-thread-only state, or
  marshal them.
- **Mass close:** closing many panes at once each does a bounded join on a pool
  thread. Cancel makes joins fast; bounded timeout caps worst case. Acceptable.

## H. Out of scope (tracked elsewhere)

- Mode B headless re-gate → #117 / AvaloniaUI/Avalonia#21467.
- `rusty_ssh` `SafeHandle` mirror → recommended #118 follow-up PR.
- Full `CompositeDisposable` adoption → not pursued (YAGNI).
