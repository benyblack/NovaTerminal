# Native SSH FFI Handle-Safety Hardening — Design

**Date:** 2026-06-10
**Issue:** #121 (items 2, 4, 3 — "sub-project A"). Item 1 (credential zeroization) is **sub-project B**, a separate spec/plan/PR.
**Branch (planned):** `fix/issue-121-ssh-ffi-handle-safety`
**Lineage:** Continues the FFI-safety work from #118/#119 (PTY `PtySafeHandle`), applying the same managed-side ref-counting to the native SSH stack and adding a Rust-side fail-closed handle model.

## Background

`#121` collects hardening items for the native SSH stack (`rusty_ssh` + `NativeSshInterop`). This spec covers three of the four; the fourth (secret zeroization) is split out because it spans a different axis (vault/options/serialization) and is larger.

Current state (verified):
- `nova_ssh_connect` returns a raw `*mut NovaSshSession` (`Box::into_raw`). Every session export (`poll_event`, `write`, `resize`, `open_direct_tcpip`, `channel_write`/`eof`/`close`, `submit_response`) does `let session = unsafe { &mut *session };`. `nova_ssh_close` does `Box::from_raw` + drop immediately — **there is no internal refcount**.
- C# `NativeSshSession` holds `IntPtr _sessionHandle`, polls on a background thread (`PollEvent`) while `Close` runs on `Dispose`, guarded only by `Interlocked.Exchange(ref _sessionHandle, IntPtr.Zero)`. That stops *new* calls but not one already past the null-check — so a `poll_event` in flight while `nova_ssh_close` frees → dangling `&mut` → **use-after-free** (the #118 race, on the SSH side).
- `NativePortForwardSession` shares the same handle.
- All exports are wrapped in `ffi_guard(NOVA_SSH_RESULT_PANIC, …)` (panic → `-7`, no unwind across FFI). Good; preserved.
- `ffi_contract.rs` (41 LOC) tests only struct layout + null-handle rejection.

## Goals / Non-goals

**Goals**
- Eliminate the poll-vs-close UAF on the SSH session handle (item 2).
- Make the Rust FFI **fail closed** on stale/closed/double-closed handles (return `INVALID_ARGUMENT`) instead of invoking UB, so abuse is safely testable (items 2 + 4).
- Add the FFI abuse-test suite to `ffi_contract.rs` and confirm it runs in CI (item 4).
- Audit + guard error-string ownership across the FFI with a debug allocation counter (item 3).

**Non-goals**
- Secret zeroization / credential `char[]` rework — **sub-project B** (#121 item 1).
- Changing SSH auth, host-key verification (reviewed, correct), or the worker/event protocol.
- The one-shot SFTP paths' handle model — `RunSftpTransfer`/`ListRemoteDirectory` take JSON and return a response with no persistent handle; they get only the error-string audit (item 3) and JSON abuse tests (item 4), not the SafeHandle migration.

## Approach decisions (locked)

- **Handle model:** C# `NovaSshSafeHandle` (managed ref-counting) **AND** a Rust generation-registry (fail-closed). Both, not either.
- **Registry mechanism:** a process-global `Mutex<HashMap<u64, Arc<NovaSshSession>>>` keyed by a **monotonic `u64` id**. Monotonic ids are never reused, so a freed id is simply absent — functionally the generation/slab scheme with less machinery and the same fail-closed property.
- **Abuse tests live in Rust** (`ffi_contract.rs`), enabled by the fail-closed registry, plus one C# disposed-handle safety test.

## B. Rust handle registry (item 2 core)

`rusty_ssh/src/lib.rs`:

- Global: `static REGISTRY: OnceLock<Mutex<HashMap<u64, Arc<NovaSshSession>>>>` + `static NEXT_ID: AtomicU64` (starts at 1). Id `0` is never issued (so the C# `SafeHandleZeroOrMinusOneIsInvalid` sees `0` ⇒ invalid).
- Session-export signatures change from `session: *mut NovaSshSession` to a pointer-sized handle token (`handle: usize`, carrying the `u64` id on the 64-bit targets we ship).
- `nova_ssh_connect`: build the session, `let id = NEXT_ID.fetch_add(1, SeqCst)`, `registry.lock().insert(id, Arc::new(session))`, return `id as usize`. On failure return `0`.
- Each session export: `lookup(handle)` → `registry.lock().get(&id).cloned()` (clone the `Arc`, release the lock immediately) → `None` ⇒ `INVALID_ARGUMENT`; `Some(arc)` ⇒ operate via `&arc`. The `Arc` clone keeps the session alive for the call's duration even if `close` races (no UAF); the absent-id check makes stale/double-close calls fail closed.
- `nova_ssh_close`: `registry.lock().remove(&id)` → `None` ⇒ `INVALID_ARGUMENT` (covers double-close); `Some(arc)` ⇒ on it: `command_tx` send `Close`, `shared.mark_closed()`, `worker` join. The `Box` is freed when the last `Arc` (registry's + any in-flight) drops.
- **Interior mutability:** because non-close exports now hold `&NovaSshSession` (via `Arc`) rather than `&mut`, the fields `close` mutates — `command_tx: Option<Sender>` and `worker: Option<JoinHandle>` — move behind `Mutex<Option<…>>`. `shared` is already shared/thread-safe; `command_tx` sends need only `&self`.

This keeps FFI calls short (enqueue a command / peek the event queue) and non-serialized across sessions; per-session work that was `&mut` is now `&self` through thread-safe fields.

## C. C# `NovaSshSafeHandle` + migration (item 2)

- New `internal sealed class NovaSshSafeHandle : SafeHandleZeroOrMinusOneIsInvalid` in the `Native` namespace; `ReleaseHandle()` → `NativeMethods.nova_ssh_close(handle)`; returns `true`.
- `NativeMethods`: keep a raw `nova_ssh_close(IntPtr)` (used by `ReleaseHandle`); change the persistent-session P/Invokes (`nova_ssh_poll_event`/`write`/`resize`/`open_direct_tcpip`/`channel_write`/`channel_eof`/`channel_close`/`submit_response`) to take `NovaSshSafeHandle`; `nova_ssh_connect` returns `NovaSshSafeHandle`.
- `INativeSshInterop`: `Connect` returns `NovaSshSafeHandle`; the session methods take `NovaSshSafeHandle` instead of `IntPtr`. The one-shot `ListRemoteDirectory`/`RunSftpTransfer` signatures are unchanged.
- `NativeSshInterop`: `Connect` returns the SafeHandle (throw if `IsInvalid`); session methods short-circuit on `handle.IsClosed || handle.IsInvalid` then call through. The marshaller's automatic `DangerousAddRef`/`Release` around each P/Invoke prevents `nova_ssh_close` (`ReleaseHandle`) from running during an in-flight `PollEvent`/`Write` — closing the poll-vs-close race.
- `NativeSshSession`: `_sessionHandle: IntPtr` → `NovaSshSafeHandle`; the `Interlocked.Exchange`/`Close(handle)` teardown becomes `_sessionHandle.Dispose()` (SafeHandle is itself thread-safe + idempotent). `NativePortForwardSession` receives and holds the same `NovaSshSafeHandle` instance (shared ownership; the runtime ref-counts).

## D. FFI abuse tests (item 4)

The abuse tests target the **handle lifecycle**, not real SSH I/O — so they must not need a live server (none exists in CI; the Docker E2E suite is `[SKIP]` off-CI). They split by what they need:

**Handle-lifecycle tests — `#[cfg(test)] mod` unit tests inside `lib.rs`** (so they can build a *stub* `NovaSshSession` — `command_tx: None`, `worker: None`, fresh `shared` — and insert it into the registry to obtain a real id without connecting):
- **call-after-close**: register a stub → `close` → `poll_event`/`write`/`resize`/`submit_response` on the same id ⇒ all `INVALID_ARGUMENT`.
- **double-close**: second `nova_ssh_close` on the id ⇒ `INVALID_ARGUMENT`.
- **concurrent poll+close**: register a stub, spawn threads hammering `poll_event` on the id while another thread `close`s ⇒ no UB; each call returns OK / `EVENT_READY` / `INVALID_ARGUMENT`, never crashes. (Run under repetition to shake out the race.)

**JSON-input tests — `rusty_ssh/tests/ffi_contract.rs` integration tests** (no session needed; these parse before connecting):
- **malformed + oversized JSON** into `nova_ssh_sftp_transfer` / `nova_ssh_sftp_list_directory` ⇒ clean error result, no panic/UB.
- Keep the existing struct-layout + null-handle tests; add the alloc-counter balance assertion (§E).

This requires the registry insert/lookup/remove helpers and a stub-session constructor to be `pub(crate)` / test-visible. Confirm `cargo test` for `rusty_ssh` (unit + integration) runs in CI (the workflow builds the crate; add/verify the test step).

One C# test: calling a session method on a disposed `NovaSshSafeHandle` is a safe no-op / throws `ObjectDisposedException` rather than crashing.

## E. Error-string ownership audit + debug counter (item 3)

- Rust: a `#[cfg(debug_assertions)]` `static OUTSTANDING_STRINGS: AtomicI64`. Increment wherever a `CString::into_raw` is handed to C# (the `sftp_transfer`/`list_directory` response paths); decrement in `nova_ssh_string_free`. A Rust test runs representative transfer/list calls (against malformed input so no network needed) and asserts the counter returns to 0.
- C# audit: confirm every Rust-allocated payload is freed — `TakeNativeUtf8AndFree` + the `finally` blocks in `RunSftpTransfer`/`ListRemoteDirectory` already do this; add a one-line ownership-contract comment at `nova_ssh_string_free`'s C# call sites. No behavior change expected; if the audit finds a leak path, fix it.

## F. Testing strategy

- **Rust** (`cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml`): registry validation, the abuse suite, the alloc-counter balance test. Wired into CI.
- **C#** (targeted, non-headless): `NovaSshSafeHandle` dispose idempotency + post-dispose safety; build of `NovaTerminal.Platform` green via `scripts/build.ps1`.
- Existing native SSH E2E tests (`NativeSshDockerE2eTests`, currently `[SKIP]` off-CI) remain unaffected; the migration must keep them compiling.

## G. Risks to validate during implementation

- **`NovaSshSession` interior-mutability refactor**: `command_tx`/`worker` → `Mutex<Option<…>>`; verify no other `&mut self` path breaks, and that `close`'s `worker.join()` (now under a lock-then-take) doesn't deadlock against a worker that calls back into the session.
- **`worker.join()` in `ReleaseHandle`** can block; runs in the Dispose/finalizer context. Confirm it terminates promptly once `Close` is signalled; bound/log if not.
- **Token vs pointer**: handle is a `u64` id carried in an `IntPtr`; assumes 64-bit (the shipped app is x64 .NET 10). Assert the registry never issues `0`.
- **Registry lock contention**: lookups clone-and-release immediately, so contention is minimal; confirm no export holds the registry lock across blocking work.
- **`NativePortForwardSession` sharing**: ensure both it and `NativeSshSession` reference the *same* `NovaSshSafeHandle` (one owner Disposes; the other must not double-dispose — SafeHandle tolerates it, but ownership should be explicit).

## H. Out of scope (tracked elsewhere)

- #121 item 1 — credential `char[]`/`byte[]` zeroization rework + Rust `zeroize` → **sub-project B** (next spec/plan/PR).
- Re-running the Docker-gated SSH E2E suite in CI — unchanged.
