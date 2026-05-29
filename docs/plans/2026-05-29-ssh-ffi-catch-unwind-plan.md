# FFI catch_unwind Guards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Guarantee no `extern "C"` function in `rusty_ssh` or `rusty_pty` can unwind a panic across the managed↔native boundary (UB → instant process abort); a panic instead degrades to a clean error code/null the .NET caller already handles.

**Architecture:** Add a tiny `ffi_guard(on_panic, || body)` helper per crate that wraps each `extern "C"` body in `std::panic::catch_unwind(AssertUnwindSafe(..))`, returning a type-appropriate default on panic. Keep the default `panic = "unwind"` (NOT `abort`, which would defeat `catch_unwind`). Make `rusty_ssh`'s `std::sync::Mutex` locks poison-tolerant so a panicked worker thread doesn't cascade. Add a managed `ResultPanic` constant for clear messaging.

**Tech Stack:** Rust 2024 (cdylib+rlib), `std::panic::catch_unwind`, cargo; .NET 10 / C# P/Invoke. Builds via `scripts/build.ps1`; Rust unit tests via `cargo test` run in each crate dir.

**Spec:** `docs/plans/2026-05-29-ssh-ffi-catch-unwind-design.md`

---

## File Structure

| File | Change |
|---|---|
| `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs` | add `ffi_guard` + `NOVA_SSH_RESULT_PANIC`; wrap 13 `extern "C"` fns; poison-tolerant locks; `#[cfg(test)]` tests |
| `src/NovaTerminal.App/native/src/lib.rs` (rusty_pty) | add `ffi_guard`; wrap 8 `extern "C"` fns; `#[cfg(test)]` test |
| `src/NovaTerminal.Platform/Ssh/Native/NativeSshInterop.cs` | add `ResultPanic = -7` constant + message |

**Crate dirs** (run `cargo` from these):
- rusty_ssh: `src/NovaTerminal.App/native/rusty_ssh`
- rusty_pty: `src/NovaTerminal.App/native`

**Out of scope:** rewriting all 45 `.unwrap()` (the guard neutralizes them), `panic = "abort"` (rejected — incompatible with `catch_unwind`), any `Cargo.toml` `[profile]` change.

---

## rusty_ssh `extern "C"` functions and their `ffi_guard` default

| Function | Return | `on_panic` default |
|---|---|---|
| `nova_ssh_connect` | `*mut NovaSshSession` | `std::ptr::null_mut()` |
| `nova_ssh_poll_event` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_write` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_resize` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_open_direct_tcpip` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_channel_write` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_channel_eof` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_channel_close` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_submit_response` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_close` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_sftp_transfer` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_sftp_list_directory` | `c_int` | `NOVA_SSH_RESULT_PANIC` |
| `nova_ssh_string_free` | `()` | `()` |

## rusty_pty `extern "C"` functions and their `ffi_guard` default

| Function | Return | `on_panic` default |
|---|---|---|
| `pty_spawn` | `*mut PtyState` | `std::ptr::null_mut()` |
| `pty_spawn_with_envs` | `*mut PtyState` | `std::ptr::null_mut()` |
| `pty_create` | `*mut PtyState` | `std::ptr::null_mut()` |
| `pty_read` | `c_int` | `-1` (existing error sentinel) |
| `pty_write` | `c_int` | `-1` (existing error sentinel) |
| `pty_get_pid` | `c_int` | `-1` (existing error sentinel) |
| `pty_resize` | `()` | `()` |
| `pty_close` | `()` | `()` |

---

## Task 1: rusty_ssh — `ffi_guard` helper + `NOVA_SSH_RESULT_PANIC` (TDD)

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs` (result-code block ~line 93–100; add helper + tests)

- [ ] **Step 1: Add the panic result code** after line 100 (`NOVA_SSH_RESULT_CANCELED`):

```rust
pub const NOVA_SSH_RESULT_PANIC: c_int = -7;
```

- [ ] **Step 2: Add the `ffi_guard` helper.** Place it immediately after the result-code constants block (after the `NOVA_SSH_RESULT_PANIC` line). Add the import at the top of the file if not already present (`use std::panic::{catch_unwind, AssertUnwindSafe};`):

```rust
/// Runs an FFI body, converting any panic into `on_panic` instead of unwinding
/// across the C boundary (which is undefined behavior). The body is asserted
/// unwind-safe because FFI bodies operate on raw pointers owned by the caller.
fn ffi_guard<R>(on_panic: R, body: impl FnOnce() -> R) -> R {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(value) => value,
        Err(_) => on_panic,
    }
}
```

- [ ] **Step 3: Write the failing test.** Add at the end of `lib.rs`:

```rust
#[cfg(test)]
mod ffi_guard_tests {
    use super::*;

    #[test]
    fn ffi_guard_returns_default_on_panic() {
        // Silence the default panic hook so the captured panic doesn't spam test output.
        let prev = std::panic::take_hook();
        std::panic::set_hook(Box::new(|_| {}));
        let rc = ffi_guard(NOVA_SSH_RESULT_PANIC, || -> c_int { panic!("boom") });
        std::panic::set_hook(prev);
        assert_eq!(rc, NOVA_SSH_RESULT_PANIC);
    }

    #[test]
    fn ffi_guard_passes_through_normal_return() {
        let rc = ffi_guard(NOVA_SSH_RESULT_PANIC, || -> c_int { NOVA_SSH_RESULT_OK });
        assert_eq!(rc, NOVA_SSH_RESULT_OK);
    }
}
```

- [ ] **Step 4: Run the tests — verify they pass.**

Run (from `src/NovaTerminal.App/native/rusty_ssh`): `cargo test ffi_guard`
Expected: `test result: ok. 2 passed`. (If `ffi_guard` is reported as dead code with `#[deny(warnings)]`, ignore for now — Task 2 uses it.)

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs
git commit -m "feat(ssh-ffi): add ffi_guard catch_unwind helper + NOVA_SSH_RESULT_PANIC (#75)"
```

---

## Task 2: rusty_ssh — wrap all 13 extern fns + poison-tolerant locks

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs` (the 13 `extern "C"` fns; the `.lock().expect("… poisoned")` sites)

- [ ] **Step 1: Wrap each `extern "C"` body in `ffi_guard`.** For every function in the rusty_ssh table above, wrap the existing body. The transform is mechanical — keep the body verbatim, indent it inside the closure. Example for `nova_ssh_poll_event` (currently lines ~589–624):

```rust
#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_poll_event(
    session: *mut NovaSshSession,
    event: *mut NovaSshEvent,
    payload: *mut u8,
    payload_capacity: usize,
) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        if session.is_null() || event.is_null() {
            return NOVA_SSH_RESULT_INVALID_ARGUMENT;
        }
        let session = unsafe { &mut *session };
        let queued = match session.shared.peek_event() {
            Some(event_value) => event_value,
            None => return NOVA_SSH_RESULT_OK,
        };
        unsafe {
            (*event).kind = queued.kind as u32;
            (*event).payload_len = queued.payload.len() as u32;
            (*event).status_code = queued.status_code;
            (*event).flags = queued.flags;
        }
        if queued.payload.len() > payload_capacity {
            return NOVA_SSH_RESULT_BUFFER_TOO_SMALL;
        }
        if !payload.is_null() && !queued.payload.is_empty() {
            unsafe {
                ptr::copy_nonoverlapping(queued.payload.as_ptr(), payload, queued.payload.len());
            }
        }
        session.shared.pop_event();
        NOVA_SSH_RESULT_EVENT_READY
    })
}
```

Apply the same wrap to the other 12 functions using the `on_panic` value from the rusty_ssh table:
- `nova_ssh_connect` → `ffi_guard(std::ptr::null_mut(), || { <body> })`
- `nova_ssh_string_free` → `ffi_guard((), || { <body> })`
- all remaining `c_int` fns → `ffi_guard(NOVA_SSH_RESULT_PANIC, || { <body> })`

Note: `return` statements inside the original bodies still work — they return from the closure, which is the fn's return value. Inner `unsafe` blocks stay as-is.

- [ ] **Step 2: Make std-Mutex locks poison-tolerant.** Replace every poison-panicking lock. These are identified by their `.expect("… poisoned")` suffix on `self.closed` / `self.responses` / `self.events` (`std::sync::Mutex`). Replace each of these three exact strings:

```
.expect("closed mutex poisoned")     →  .unwrap_or_else(|e| e.into_inner())
.expect("responses mutex poisoned")  →  .unwrap_or_else(|e| e.into_inner())
.expect("events mutex poisoned")     →  .unwrap_or_else(|e| e.into_inner())
```

So e.g. `self.closed.lock().expect("closed mutex poisoned")` becomes `self.closed.lock().unwrap_or_else(|e| e.into_inner())`. This covers the single-line and multi-line (`.lock()\n.expect(...)`) occurrences alike.

- [ ] **Step 3: Catch any remaining bare std-Mutex unwraps.** Search for stragglers:

Run (from `src/NovaTerminal.App/native/rusty_ssh`): `rg -n "\.lock\(\)\s*\.\s*(unwrap|expect)" src/lib.rs`
Expected: **no matches**. If any remain on a `std::sync::Mutex`, convert them to `.unwrap_or_else(|e| e.into_inner())` too. (Leave `tokio::sync::Mutex` `.lock().await` calls untouched — they don't poison and return a guard directly.)

- [ ] **Step 4: Verify every extern fn is guarded.** Run: `rg -n "pub extern \"C\" fn" src/lib.rs` → 13 functions; spot-check that each body now begins with `ffi_guard(`.

- [ ] **Step 5: Build + test.**

Run (from `src/NovaTerminal.App/native/rusty_ssh`): `cargo build --release` then `cargo test`
Expected: build succeeds (0 errors); all tests pass.

- [ ] **Step 6: Add a boundary smoke test** at the end of the `ffi_guard_tests` module — call a real guarded fn with a null arg and assert it returns a defined code, not a crash:

```rust
    #[test]
    fn poll_event_rejects_null_without_panic() {
        let rc = nova_ssh_poll_event(std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut(), 0);
        assert_eq!(rc, NOVA_SSH_RESULT_INVALID_ARGUMENT);
    }
```

Run: `cargo test` → passes.

- [ ] **Step 7: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs
git commit -m "fix(ssh-ffi): guard all extern \"C\" fns with catch_unwind; poison-tolerant locks (#75)"
```

---

## Task 3: rusty_pty — `ffi_guard` helper + wrap all 8 extern fns (TDD)

**Files:**
- Modify: `src/NovaTerminal.App/native/src/lib.rs` (top of file; the 8 `extern "C"` fns; tests at end)

- [ ] **Step 1: Add the `ffi_guard` helper** near the top of `lib.rs` (after the existing `use` block). Add `use std::panic::{catch_unwind, AssertUnwindSafe};` if not present:

```rust
/// Runs an FFI body, converting any panic into `on_panic` instead of unwinding
/// across the C boundary (undefined behavior). Asserted unwind-safe: FFI bodies
/// operate on raw pointers owned by the caller.
fn ffi_guard<R>(on_panic: R, body: impl FnOnce() -> R) -> R {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(value) => value,
        Err(_) => on_panic,
    }
}
```

- [ ] **Step 2: Write the failing test** at the end of `lib.rs`:

```rust
#[cfg(test)]
mod ffi_guard_tests {
    use super::*;

    #[test]
    fn ffi_guard_returns_default_on_panic() {
        let prev = std::panic::take_hook();
        std::panic::set_hook(Box::new(|_| {}));
        let rc = ffi_guard(-1, || -> c_int { panic!("boom") });
        std::panic::set_hook(prev);
        assert_eq!(rc, -1);
    }
}
```

- [ ] **Step 3: Run the test.**

Run (from `src/NovaTerminal.App/native`): `cargo test ffi_guard`
Expected: `1 passed`.

- [ ] **Step 4: Wrap each of the 8 `extern "C"` bodies** in `ffi_guard` using the `on_panic` value from the rusty_pty table (pointer fns → `std::ptr::null_mut()`, `c_int` fns → `-1`, void fns → `()`). Same mechanical transform as Task 2 Step 1: keep the body verbatim inside `ffi_guard(<default>, || { ... })`.

- [ ] **Step 5: Verify all guarded + build + test.**

Run (from `src/NovaTerminal.App/native`): `rg -n "pub extern \"C\" fn" src/lib.rs` → 8 fns, each body begins with `ffi_guard(`.
Run: `cargo build --release` then `cargo test`
Expected: build 0 errors; tests pass. (rusty_pty locks are already `if let Ok(..) = ..lock()` poison-tolerant — no lock changes needed.)

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/native/src/lib.rs
git commit -m "fix(pty-ffi): guard all extern \"C\" fns with catch_unwind (#75)"
```

---

## Task 4: Managed — `ResultPanic` constant for clear messaging

**Files:**
- Modify: `src/NovaTerminal.Platform/Ssh/Native/NativeSshInterop.cs` (the result-code constants block, ~lines 12–17)

- [ ] **Step 1: Add the constant** after `ResultCanceled` (line ~17):

```csharp
    private const int ResultPanic = -7;
```

- [ ] **Step 2: Give it a clear message where result codes are stringified.** Find the failure-message helper(s) (e.g. `BuildSftpTransferFailureMessage` / the `throw new InvalidOperationException($"Native SSH ... result {rc}.")` sites). Add a `ResultPanic`-specific branch in the shared message path. If there is a single `switch`/`if` that maps codes to text, add:

```csharp
            ResultPanic => "the native SSH layer caught an internal panic (this is a bug; the session was aborted safely)",
```

If codes are only interpolated raw (no central mapping), add a guard before the generic throw in the SFTP/list paths, e.g.:

```csharp
            if (rc == ResultPanic)
            {
                throw new InvalidOperationException("Native SSH operation failed: internal panic caught at the FFI boundary.");
            }
```

(Exact insertion point: mirror the existing `if (rc == ResultCanceled)` pattern already present in `nova_ssh_sftp_transfer` and `nova_ssh_sftp_list_directory` wrappers. Safety does not depend on this — the existing `rc != ResultOk` paths already throw on `-7` — this only improves the message.)

- [ ] **Step 3: Build the managed assembly.**

Run (from repo root): `scripts/build.ps1 build src/NovaTerminal.Platform/NovaTerminal.Platform.csproj`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/NovaTerminal.Platform/Ssh/Native/NativeSshInterop.cs
git commit -m "feat(ssh): surface NOVA_SSH_RESULT_PANIC with a clear managed message (#75)"
```

---

## Task 5: Final verification

**Files:** none (verification only)

- [ ] **Step 1: Both crates build in release.**

Run (from repo root):
`(cd src/NovaTerminal.App/native/rusty_ssh && cargo build --release)`
`(cd src/NovaTerminal.App/native && cargo build --release)`
Expected: both succeed, 0 errors.

- [ ] **Step 2: Rust tests pass in both crates.**

Run: `(cd src/NovaTerminal.App/native/rusty_ssh && cargo test)` and `(cd src/NovaTerminal.App/native && cargo test)`
Expected: all pass (incl. the `ffi_guard` panic tests proving no abort).

- [ ] **Step 3: No unguarded extern fn remains.**

Run: `rg -n -A1 "pub extern \"C\" fn" src/NovaTerminal.App/native/rusty_ssh/src/lib.rs src/NovaTerminal.App/native/src/lib.rs`
Expected: every signature's next non-signature line opens `ffi_guard(`. (Multi-line signatures: the line after the `) -> T {` opens `ffi_guard(`.)

- [ ] **Step 4: Confirm no `panic = "abort"` was added.**

Run: `rg -n "panic" src/NovaTerminal.App/native/rusty_ssh/Cargo.toml src/NovaTerminal.App/native/Cargo.toml`
Expected: **no matches** (default `unwind` preserved).

- [ ] **Step 5: .NET SSH + PTY suites green** (the native libs rebuild via MSBuild targets).

Run (from repo root):
`scripts/build.ps1 test tests/NovaTerminal.Platform.Tests/NovaTerminal.Platform.Tests.csproj`
`scripts/build.ps1 test tests/NovaTerminal.Pty.Tests/NovaTerminal.Pty.Tests.csproj` *(if such a project exists; otherwise the Pty tests live in `NovaTerminal.App.Tests` — run that: `scripts/build.ps1 test tests/NovaTerminal.App.Tests/NovaTerminal.App.Tests.csproj`)*
Expected: 0 failures (Docker-gated SSH E2E may skip).

- [ ] **Step 6: Confirm acceptance criteria**
- [ ] No `extern "C"` fn in either crate can unwind a panic (Step 3 + the `ffi_guard` design).
- [ ] A forced panic degrades to a sentinel/null, proven by the `ffi_guard_returns_default_on_panic` tests, not a process abort.
- [ ] Both crates build release-clean; .NET suites pass.

- [ ] **Step 7: Push and open PR (only on user request)**

```bash
git push -u origin feature/issue-75-ssh-ffi-catch-unwind
gh pr create --base main --title "Guard native FFI boundary with catch_unwind (rusty_ssh + rusty_pty) (#75)" --body-file <(...)
```
PR body closes #75. Do not push without explicit user confirmation.
