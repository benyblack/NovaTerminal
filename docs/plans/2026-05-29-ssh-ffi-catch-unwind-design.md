# Design: catch_unwind guards on the native FFI boundary (rusty_ssh + rusty_pty)

**Date:** 2026-05-29
**Issue:** [#75](https://github.com/benyblack/NovaTerminal/issues/75) — "rusty_ssh FFI: no catch_unwind guards on extern \"C\" boundary (panic = instant process abort)"
**Status:** Approved design — ready for implementation plan

---

## Problem

`src/NovaTerminal.App/native/rusty_ssh/src/lib.rs` exposes 13 `#[no_mangle] extern "C"` functions with **zero `catch_unwind` guards**, while the body contains 45 `.unwrap()`, 4 `panic!`/`unreachable!`, and 20 `.lock()` (4 of which are poison-prone `std::sync::Mutex::lock().unwrap()`). A Rust panic unwinding across an `extern "C"` boundary is **undefined behavior**; with the crate's default `panic = "unwind"`, hitting any panic inside an FFI call — or locking a mutex poisoned by a panicked worker thread — aborts the process instantly with no .NET exception, no log, no WER managed event. This is the same user-visible signature as the #74 GlobalHotkey crash, still latent. `rusty_pty` (`src/NovaTerminal.App/native/src/lib.rs`, 8 `extern "C"` fns) has the identical pattern and risk.

## Goals / Acceptance

- No `extern "C"` function in `rusty_ssh` **or** `rusty_pty` can unwind a panic across the boundary.
- A forced panic (on the calling thread or a worker thread that poisons a shared mutex) degrades to a clean error surfaced to the managed caller — never a process abort.
- `cargo build --release` for both crates is clean; existing tests pass; new tests prove the guard catches a forced panic.

## Decisions

### 1. Scope: both crates
Harden `rusty_ssh` **and** `rusty_pty` in this PR. The guard helper and test pattern are identical, so covering both closes the whole latent class at once.

### 2. Mechanism: a small `ffi_guard` helper wrapping `catch_unwind` — keep `panic = "unwind"`
Add one helper per crate (the crates don't share a lib, so each gets its own copy — a few lines):

```rust
use std::panic::{catch_unwind, AssertUnwindSafe};

/// Run an FFI body, converting any panic into `on_panic` instead of unwinding
/// across the C boundary (which is UB). The closure is asserted unwind-safe
/// because FFI bodies operate on raw pointers the caller owns.
fn ffi_guard<R>(on_panic: R, body: impl FnOnce() -> R) -> R {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(value) => value,
        Err(_) => on_panic,
    }
}
```

Every `extern "C"` body becomes `ffi_guard(<default>, || { <existing body> })`. The default matches the return type:
- `c_int`-returning fns → a panic sentinel result code (see §3).
- pointer-returning fns (`nova_ssh_connect`; `pty_spawn`, `pty_spawn_with_envs`, `pty_create`) → `std::ptr::null_mut()`.
- void fns (`nova_ssh_string_free`; `pty_resize`, `pty_close`) → `()` (swallow; the panic is contained, nothing to report).

**`panic = "abort"` is explicitly rejected.** It is mutually exclusive with `catch_unwind` — under `abort`, a panic terminates the process before `catch_unwind` can run, defeating the entire fix. We keep the default `panic = "unwind"` so panics degrade to errors. (The issue floated `abort` as "defense-in-depth," but with every boundary guarded there is no "missed panic" for `abort` to make deterministic, and enabling it would re-introduce the abort we are removing.)

### 3. Panic sentinel result codes
- **rusty_ssh:** add `NOVA_SSH_RESULT_PANIC: c_int = -7`. Used as the `ffi_guard` default for all `c_int`-returning SSH fns. Mirror it managed-side in `NativeSshInterop.cs` as `private const int ResultPanic = -7;` with a clear failure message. (Managed safety already holds without this — the existing `rc != ResultOk` paths throw on any unknown negative — but the explicit constant gives an actionable message instead of "failed with result -7".)
- **rusty_pty:** `pty_read`/`pty_write` return `c_int` byte counts with negative = error; their `ffi_guard` default is the crate's existing read/write error sentinel (confirm the exact value during planning; reuse it rather than inventing a new one). Pointer/void fns as in §2.

### 4. Mutex poison tolerance
Replace the 4 (and any in pty) `std::sync::Mutex::lock().unwrap()` / `.expect(...)` sites with `lock().unwrap_or_else(|e| e.into_inner())` where the guarded state remains usable after a worker-thread panic. This keeps a session alive across a poisoned lock instead of every subsequent FFI call degrading to an error. (`tokio::sync::Mutex` locks — the majority of the 20 — don't poison and are unchanged.)

### 5. Hot-path error returns (quality refinement, not a safety requirement)
Because the boundary guard (§2) already neutralizes all 45 `.unwrap()`s, a full unwrap-rewrite is **out of scope**. As a refinement, convert the unwraps on the hottest path — `nova_ssh_poll_event` → `peek_event`/`pop_event` and the write path — to explicit error returns so a recoverable condition there yields a precise code (e.g. `CLOSED`) rather than the generic `PANIC` sentinel. Keep this narrow.

## Out of scope
- Rewriting all 45 `.unwrap()` / 4 `panic!` sites (the guard covers them).
- Any `russh`/`tokio` behavioral change, the byte-vs-string PTY boundary (separate tech-debt), or SFTP protocol changes.
- `panic = "abort"` (rejected above).

## Testing
- **Rust unit tests (per crate):** a test that calls `ffi_guard(SENTINEL, || panic!("boom"))` and asserts it returns `SENTINEL` (proves the guard catches and does not abort). A second test forces a poisoned `std::sync::Mutex` and asserts the poison-tolerant lock still yields the inner value.
- **Boundary smoke test:** drive one real `extern "C"` fn (e.g. `nova_ssh_poll_event`) with inputs that previously panicked and assert it returns the sentinel/`CLOSED` rather than aborting.
- **Build gate:** `cargo build --release` for both crates + the existing .NET suites (`scripts/build.ps1 test tests/NovaTerminal.Platform.Tests/...` and the Pty tests) stay green. The native crates build via the existing MSBuild `BuildRustSshNativeForTests` / pty targets.

## Files (anticipated)
- `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs` — `ffi_guard`, `NOVA_SSH_RESULT_PANIC`, wrap 13 fns, poison-tolerant locks, hot-path returns, tests.
- `src/NovaTerminal.App/native/src/lib.rs` (rusty_pty) — `ffi_guard`, wrap 8 fns, poison-tolerant locks, tests.
- `src/NovaTerminal.Platform/Ssh/Native/NativeSshInterop.cs` — `ResultPanic` constant + message.
- Neither `Cargo.toml` changes (no `panic` profile added — confirming the default stays `unwind`).
