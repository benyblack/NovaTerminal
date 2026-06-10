# Native SSH FFI Handle-Safety Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the native SSH FFI fail closed on stale/closed/double-closed session handles (Rust monotonic-id registry) and ref-counted on the managed side (`NovaSshSafeHandle`), eliminating the poll-vs-close use-after-free, with an FFI abuse-test suite and an error-string allocation audit.

**Architecture:** Replace the raw `*mut NovaSshSession` FFI handle with an opaque `u64` id into a process-global `Mutex<HashMap<u64, Arc<NovaSshSession>>>`; every session export validates the id (absent ⇒ `INVALID_ARGUMENT`) and operates through a cloned `Arc` so an in-flight call can't be freed by a concurrent close. On the C# side, a `NovaSshSafeHandle : SafeHandleZeroOrMinusOneIsInvalid` carries the id and gives the marshaller AddRef/Release around every call.

**Tech Stack:** Rust (`russh`, `std::sync`), C# (.NET 10, `Microsoft.Win32.SafeHandles`), xUnit, `cargo test`.

**Spec:** `docs/superpowers/specs/2026-06-10-ssh-ffi-handle-safety-design.md`
**Issue:** #121 items 2/4/3 (item 1 = separate sub-project B).

**Build/test commands (from repo root):**
- Rust SSH: `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml`
- Rust SSH build (release, for C# to load): `cargo build --release --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml`
- C# build: `scripts/build.ps1 build src/NovaTerminal.Platform`
- C# tests (targeted): `scripts/build.ps1 test tests/NovaTerminal.Platform.Tests --filter "FullyQualifiedName~NativeSshSafeHandle"`
- ALWAYS use `scripts/build.ps1` / `scripts/build.sh`, never raw `dotnet build` (hangs on piped stdout — see CLAUDE.md).

---

## File Structure

| File | Responsibility | Tasks |
|---|---|---|
| `rusty_ssh/src/lib.rs` | Handle registry + interior mutability + export migration + error-string counter + lifecycle abuse unit tests | 1, 2, 4 |
| `rusty_ssh/tests/ffi_contract.rs` | Updated null tests + JSON-malformed abuse tests + counter balance | 3 |
| `Ssh/Native/NativeSshSafeHandle.cs` (new) | `SafeHandle` subclass owning the session id | 5 |
| `Ssh/Native/NativeSshInterop.cs` + `INativeSshInterop.cs` | P/Invoke + interface migrated to `NovaSshSafeHandle` | 5, 6 |
| `Ssh/Sessions/NativeSshSession.cs` | `_sessionHandle` → `NovaSshSafeHandle`; Dispose | 6 |
| `Ssh/Native/NativePortForwardSession.cs` | hold the shared `NovaSshSafeHandle` | 6 |
| `tests/NovaTerminal.Platform.Tests/Ssh/NativeSshSafeHandleTests.cs` (new) | disposed-handle safety | 7 |
| `.github/workflows/ci.yml` | ensure `rusty_ssh` `cargo test` runs | 8 |

---

## Part 1 — Rust handle registry (item 2)

### Task 1: Registry + interior mutability + migrate all session exports

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs` (struct 117-121; constructions ~594, ~2817, ~2978; exports 602-844; close 846-867; connect ~555-600)
- Modify: `src/NovaTerminal.App/native/rusty_ssh/tests/ffi_contract.rs` (null-handle test signatures)

This is a cohesive refactor; the existing tests (`ffi_contract.rs`, the `ffi_guard` unit tests) are the regression guard. End state: `cargo test` green.

- [ ] **Step 1: Add registry statics + helpers**

Near the top of `lib.rs` (after the `use` block; add any missing imports: `std::collections::HashMap`, `std::sync::{Arc, Mutex, OnceLock}`, `std::sync::atomic::{AtomicU64, Ordering}` — `Arc`/`Mutex` may already be imported):

```rust
static SESSION_REGISTRY: OnceLock<Mutex<HashMap<u64, Arc<NovaSshSession>>>> = OnceLock::new();
static NEXT_SESSION_ID: AtomicU64 = AtomicU64::new(1);

fn session_registry() -> &'static Mutex<HashMap<u64, Arc<NovaSshSession>>> {
    SESSION_REGISTRY.get_or_init(|| Mutex::new(HashMap::new()))
}

// Lock the registry, recovering from poisoning (the lock is held only for brief
// map ops with no user code, so a poisoned guard is still structurally valid).
fn lock_registry() -> std::sync::MutexGuard<'static, HashMap<u64, Arc<NovaSshSession>>> {
    session_registry().lock().unwrap_or_else(|p| p.into_inner())
}

/// Insert a session, returning a fresh non-zero handle id (id 0 is never issued,
/// so the C# SafeHandleZeroOrMinusOneIsInvalid treats 0 as invalid).
fn registry_insert(session: NovaSshSession) -> u64 {
    let id = NEXT_SESSION_ID.fetch_add(1, Ordering::SeqCst);
    lock_registry().insert(id, Arc::new(session));
    id
}

/// Look up a live session by handle token. None ⇒ unknown/closed/stale handle.
fn registry_get(handle: usize) -> Option<Arc<NovaSshSession>> {
    let id = handle as u64;
    if id == 0 {
        return None;
    }
    lock_registry().get(&id).cloned()
}

/// Remove (close) a session, returning it if it was present. A second call for
/// the same id returns None (covers double-close).
fn registry_remove(handle: usize) -> Option<Arc<NovaSshSession>> {
    let id = handle as u64;
    if id == 0 {
        return None;
    }
    lock_registry().remove(&id)
}
```

- [ ] **Step 2: Move `command_tx`/`worker` behind interior mutability**

Change the struct (117-121):

```rust
pub struct NovaSshSession {
    shared: Arc<SharedState>,
    command_tx: Mutex<Option<mpsc::UnboundedSender<WorkerCommand>>>,
    worker: Mutex<Option<thread::JoinHandle<()>>>,
}
```

Update the three construction sites — `nova_ssh_connect` (~594), and the two others (~2817, ~2978). Wrap the values:
- where it currently sets `command_tx: Some(command_tx)` → `command_tx: Mutex::new(Some(command_tx))`
- `command_tx: None` → `command_tx: Mutex::new(None)`
- `worker: Some(worker)` → `worker: Mutex::new(Some(worker))`
- `worker: None` → `worker: Mutex::new(None)`

Add a small helper for the repeated command-send pattern (used by write/resize/channel_*):

```rust
// Send a worker command through the session's command channel, mapping the
// channel state to an FFI result. Returns CLOSED if the worker is gone.
fn send_command(session: &NovaSshSession, command: WorkerCommand) -> c_int {
    let guard = session.command_tx.lock().unwrap_or_else(|p| p.into_inner());
    match guard.as_ref() {
        Some(tx) => tx
            .send(command)
            .map(|_| NOVA_SSH_RESULT_OK)
            .unwrap_or(NOVA_SSH_RESULT_CLOSED),
        None => NOVA_SSH_RESULT_CLOSED,
    }
}
```

- [ ] **Step 3: Migrate `nova_ssh_connect` to return a handle id**

In `nova_ssh_connect` (~555-600), it currently ends `Box::into_raw(Box::new(session))` returning `*mut NovaSshSession`. Change the return type to `usize` and the tail to register. The signature becomes:

```rust
pub extern "C" fn nova_ssh_connect(args: *const NovaSshConnectArgs) -> usize {
```

Wrap the body in `ffi_guard(0, || { ... })` (return `0` on panic), and replace the final `Box::into_raw(Box::new(session))` with:

```rust
        registry_insert(session) as usize
```

(Every early `return std::ptr::null_mut();` / error path in `connect` becomes `return 0;`.)

- [ ] **Step 4: Migrate the session exports to the handle token + registry lookup**

Rewrite each export. Signatures change `session: *mut NovaSshSession` → `handle: usize`; the null check + `&mut *session` become a registry lookup.

`nova_ssh_poll_event`:

```rust
pub extern "C" fn nova_ssh_poll_event(
    handle: usize,
    event: *mut NovaSshEvent,
    payload: *mut u8,
    payload_capacity: usize,
) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        if event.is_null() {
            return NOVA_SSH_RESULT_INVALID_ARGUMENT;
        }
        let session = match registry_get(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
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

`nova_ssh_write`:

```rust
pub extern "C" fn nova_ssh_write(handle: usize, data: *const u8, data_len: usize) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        if data.is_null() && data_len != 0 {
            return NOVA_SSH_RESULT_INVALID_ARGUMENT;
        }
        let session = match registry_get(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
        let bytes = if data_len == 0 {
            Vec::new()
        } else {
            unsafe { std::slice::from_raw_parts(data, data_len) }.to_vec()
        };
        send_command(&session, WorkerCommand::Write(bytes))
    })
}
```

`nova_ssh_resize`:

```rust
pub extern "C" fn nova_ssh_resize(handle: usize, cols: u16, rows: u16) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        if cols == 0 || rows == 0 {
            return NOVA_SSH_RESULT_INVALID_ARGUMENT;
        }
        let session = match registry_get(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
        send_command(&session, WorkerCommand::Resize { cols, rows })
    })
}
```

`nova_ssh_open_direct_tcpip` (keeps its reply-channel logic; only handle + lookup + command-send change):

```rust
pub extern "C" fn nova_ssh_open_direct_tcpip(
    handle: usize,
    args: *const NovaSshDirectTcpIpArgs,
) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        if args.is_null() {
            return NOVA_SSH_RESULT_INVALID_ARGUMENT;
        }
        let args = unsafe { args.as_ref() }.expect("validated non-null args");
        let host_to_connect = match read_c_string(args.host_to_connect) {
            Some(value) => value,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
        let originator_address =
            read_c_string(args.originator_address).unwrap_or_else(|| "127.0.0.1".to_owned());
        let session = match registry_get(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
        let (reply_tx, reply_rx) = std_mpsc::channel();
        let command = WorkerCommand::OpenDirectTcpIp {
            host_to_connect,
            port_to_connect: if args.port_to_connect == 0 { 0 } else { args.port_to_connect as u32 },
            originator_address,
            originator_port: args.originator_port as u32,
            reply: reply_tx,
        };
        {
            let guard = session.command_tx.lock().unwrap_or_else(|p| p.into_inner());
            match guard.as_ref() {
                Some(tx) => {
                    if tx.send(command).is_err() {
                        return NOVA_SSH_RESULT_CLOSED;
                    }
                }
                None => return NOVA_SSH_RESULT_CLOSED,
            }
        }
        match reply_rx.recv() {
            Ok(Ok(channel_id)) => channel_id as c_int,
            Ok(Err(_)) => NOVA_SSH_RESULT_CHANNEL_OPEN_FAILED,
            Err(_) => NOVA_SSH_RESULT_CLOSED,
        }
    })
}
```

`nova_ssh_channel_write`:

```rust
pub extern "C" fn nova_ssh_channel_write(
    handle: usize,
    channel_id: u32,
    data: *const u8,
    data_len: usize,
) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        if data.is_null() && data_len != 0 {
            return NOVA_SSH_RESULT_INVALID_ARGUMENT;
        }
        let session = match registry_get(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
        let bytes = if data_len == 0 {
            Vec::new()
        } else {
            unsafe { std::slice::from_raw_parts(data, data_len) }.to_vec()
        };
        send_command(&session, WorkerCommand::WriteForwardChannel { channel_id, data: bytes })
    })
}
```

`nova_ssh_channel_eof`:

```rust
pub extern "C" fn nova_ssh_channel_eof(handle: usize, channel_id: u32) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        let session = match registry_get(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
        send_command(&session, WorkerCommand::ForwardChannelEof { channel_id })
    })
}
```

`nova_ssh_channel_close`:

```rust
pub extern "C" fn nova_ssh_channel_close(handle: usize, channel_id: u32) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        let session = match registry_get(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
        send_command(&session, WorkerCommand::CloseForwardChannel { channel_id })
    })
}
```

`nova_ssh_submit_response`:

```rust
pub extern "C" fn nova_ssh_submit_response(
    handle: usize,
    response_kind: u32,
    data: *const u8,
    data_len: usize,
) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        if data.is_null() && data_len != 0 {
            return NOVA_SSH_RESULT_INVALID_ARGUMENT;
        }
        let kind = match response_kind {
            1 => NovaSshResponseKind::HostKeyDecision,
            2 => NovaSshResponseKind::Password,
            3 => NovaSshResponseKind::Passphrase,
            4 => NovaSshResponseKind::KeyboardInteractive,
            _ => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
        let session = match registry_get(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
        let payload = if data_len == 0 {
            Vec::new()
        } else {
            unsafe { std::slice::from_raw_parts(data, data_len) }.to_vec()
        };
        session.shared.queue_response(QueuedResponse { kind, payload });
        NOVA_SSH_RESULT_OK
    })
}
```

- [ ] **Step 5: Migrate `nova_ssh_close` to remove from the registry**

```rust
pub extern "C" fn nova_ssh_close(handle: usize) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        let session = match registry_remove(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };
        if let Some(tx) = session.command_tx.lock().unwrap_or_else(|p| p.into_inner()).take() {
            let _ = tx.send(WorkerCommand::Close);
        }
        session.shared.mark_closed();
        if let Some(worker) = session.worker.lock().unwrap_or_else(|p| p.into_inner()).take() {
            let _ = worker.join();
        }
        NOVA_SSH_RESULT_OK
    })
}
```

(Note: `session` is now `Arc<NovaSshSession>`; the `Box` is freed when the last `Arc` — registry's plus any in-flight call's clone — drops. The worker join happens on the removed Arc.)

- [ ] **Step 6: Update the existing `ffi_contract.rs` null tests to the new signatures**

In `invalid_handles_are_rejected_cleanly`, the calls now pass a handle `0usize` instead of `std::ptr::null_mut()`:

```rust
    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_poll_event(0, &mut event, std::ptr::null_mut(), 0)
    );
    assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, nova_ssh_resize(0, 120, 30));
    assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, nova_ssh_write(0, [1u8].as_ptr(), 1));
    assert_eq!(
        NOVA_SSH_RESULT_INVALID_ARGUMENT,
        nova_ssh_submit_response(0, 1, br#"{}"#.as_ptr(), 2)
    );
    assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, nova_ssh_close(0));
```

- [ ] **Step 7: Build + run the Rust suite**

Run: `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml`
Expected: builds; all existing tests pass (`ffi_struct_layout_stays_stable`, `invalid_handles_are_rejected_cleanly`, `ffi_guard_*`). Fix any compile errors from missed `command_tx`/`worker` access sites (search the file for `.command_tx` and `.worker` to confirm every read goes through `.lock()`).

- [ ] **Step 8: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs src/NovaTerminal.App/native/rusty_ssh/tests/ffi_contract.rs
git commit -m "refactor(ssh-ffi): registry-backed handle ids, fail-closed on stale (#121 #118)"
```
End the commit body with a blank line then:
`Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

---

### Task 2: Handle-lifecycle abuse unit tests (item 4)

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs` (add a `#[cfg(test)] mod handle_abuse_tests` at end)

These run inside the crate so they can build a stub session (no live server) and use `registry_insert`.

- [ ] **Step 1: Add a test-only stub constructor**

Add (inside the crate, gated for tests):

```rust
#[cfg(test)]
fn stub_session() -> NovaSshSession {
    NovaSshSession {
        shared: Arc::new(SharedState::new()),
        command_tx: Mutex::new(None),
        worker: Mutex::new(None),
    }
}
```

- [ ] **Step 2: Write the abuse tests**

```rust
#[cfg(test)]
mod handle_abuse_tests {
    use super::*;

    #[test]
    fn calls_after_close_fail_closed() {
        let handle = registry_insert(stub_session()) as usize;
        assert_eq!(NOVA_SSH_RESULT_OK, nova_ssh_close(handle));

        let mut event = NovaSshEvent::default();
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, nova_ssh_poll_event(handle, &mut event, std::ptr::null_mut(), 0));
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, nova_ssh_write(handle, [1u8].as_ptr(), 1));
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, nova_ssh_resize(handle, 80, 24));
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, nova_ssh_channel_eof(handle, 0));
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, nova_ssh_submit_response(handle, 2, br#"{}"#.as_ptr(), 2));
    }

    #[test]
    fn double_close_is_rejected() {
        let handle = registry_insert(stub_session()) as usize;
        assert_eq!(NOVA_SSH_RESULT_OK, nova_ssh_close(handle));
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, nova_ssh_close(handle));
    }

    #[test]
    fn concurrent_poll_and_close_never_crashes() {
        for _ in 0..200 {
            let handle = registry_insert(stub_session()) as usize;
            let poller = std::thread::spawn(move || {
                let mut event = NovaSshEvent::default();
                for _ in 0..50 {
                    let rc = nova_ssh_poll_event(handle, &mut event, std::ptr::null_mut(), 0);
                    // OK / no-event / EVENT_READY / closed are all acceptable; never UB.
                    assert!(matches!(
                        rc,
                        NOVA_SSH_RESULT_OK | NOVA_SSH_RESULT_EVENT_READY | NOVA_SSH_RESULT_INVALID_ARGUMENT
                    ));
                }
            });
            let closer = std::thread::spawn(move || nova_ssh_close(handle));
            poller.join().unwrap();
            let _ = closer.join().unwrap();
        }
    }
}
```

- [ ] **Step 3: Run**

Run: `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml handle_abuse`
Expected: PASS (all three). The concurrent test repeats 200× to shake out the race.

- [ ] **Step 4: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs
git commit -m "test(ssh-ffi): handle-lifecycle abuse tests (call-after-close, double-close, race) (#121)"
```
(+ Co-Authored-By trailer.)

---

### Task 3: JSON-malformed abuse tests (item 4)

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/tests/ffi_contract.rs`

- [ ] **Step 1: Add malformed/oversized JSON tests**

The `nova_ssh_sftp_transfer` / `nova_ssh_sftp_list_directory` exports parse JSON before connecting, so they reject bad input without a server. Add `nova_ssh_sftp_list_directory` and `nova_ssh_string_free` to the `use` imports, then:

```rust
use std::ffi::CString;
use rusty_ssh::{nova_ssh_sftp_list_directory, nova_ssh_string_free};

#[test]
fn malformed_json_is_rejected_without_panic() {
    let bad = CString::new("{ this is not valid json ").unwrap();
    let mut response: *mut std::os::raw::c_char = std::ptr::null_mut();
    let rc = nova_ssh_sftp_list_directory(bad.as_ptr(), &mut response);
    assert_ne!(rc, 0, "malformed JSON must not report success");
    if !response.is_null() {
        unsafe { nova_ssh_string_free(response) };
    }
}

#[test]
fn oversized_json_is_rejected_without_panic() {
    // A syntactically-valid but semantically-bogus, very large payload.
    let big = format!(r#"{{"junk":"{}"}}"#, "A".repeat(2_000_000));
    let payload = CString::new(big).unwrap();
    let mut response: *mut std::os::raw::c_char = std::ptr::null_mut();
    let rc = nova_ssh_sftp_list_directory(payload.as_ptr(), &mut response);
    assert_ne!(rc, 0, "bogus oversized JSON must not report success");
    if !response.is_null() {
        unsafe { nova_ssh_string_free(response) };
    }
}
```

> The exact P/Invoke signature of `nova_ssh_sftp_list_directory` is `(*const c_char, *mut *mut c_char) -> c_int`. Confirm the parameter/return types against `lib.rs` and adjust the `use`/types if the response out-param differs; do not change the export.

- [ ] **Step 2: Run**

Run: `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml`
Expected: all pass, including the two new JSON tests and the (now id-`0`) null tests.

- [ ] **Step 3: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/tests/ffi_contract.rs
git commit -m "test(ssh-ffi): malformed/oversized JSON abuse tests (#121)"
```
(+ Co-Authored-By trailer.)

---

## Part 2 — Error-string allocation audit (item 3)

### Task 4: Debug allocation counter + balance test

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`

- [ ] **Step 1: Add the debug counter and hook it into the string alloc/free boundary**

Add near the registry statics:

```rust
#[cfg(debug_assertions)]
static OUTSTANDING_FFI_STRINGS: AtomicI64 = AtomicI64::new(0);

// Convert an owned String into a freshly-allocated C string handed to the caller.
// In debug builds, tracks the outstanding count so tests can assert alloc/free balance.
fn ffi_string_into_raw(value: String) -> *mut c_char {
    match CString::new(value) {
        Ok(c) => {
            #[cfg(debug_assertions)]
            OUTSTANDING_FFI_STRINGS.fetch_add(1, Ordering::SeqCst);
            c.into_raw()
        }
        Err(_) => std::ptr::null_mut(),
    }
}
```

Add the `AtomicI64` import (`std::sync::atomic::AtomicI64`). Then route every existing `CString::new(...).into_raw()` / `CString::into_raw(...)` that returns a payload to C# (the `sftp_transfer` and `list_directory` response builders) through `ffi_string_into_raw(...)`. (Search `lib.rs` for `into_raw` to find them; the SFTP/list response paths are the ones returning `*mut c_char` to C#.)

Update `nova_ssh_string_free` to decrement:

```rust
pub extern "C" fn nova_ssh_string_free(value: *mut c_char) {
    ffi_guard((), || {
        if !value.is_null() {
            #[cfg(debug_assertions)]
            OUTSTANDING_FFI_STRINGS.fetch_sub(1, Ordering::SeqCst);
            drop(unsafe { CString::from_raw(value) });
        }
    });
}
```

> If `nova_ssh_string_free` isn't already `ffi_guard`-wrapped, wrap it as shown (the existing body is the `if !value.is_null() { drop(CString::from_raw(value)) }` line).

- [ ] **Step 2: Write the balance test**

Add to the `#[cfg(test)] mod handle_abuse_tests` (or a new `#[cfg(test)] mod alloc_balance_tests`):

```rust
#[cfg(all(test, debug_assertions))]
mod alloc_balance_tests {
    use super::*;
    use std::ffi::CString;

    #[test]
    fn malformed_list_request_frees_its_response_string() {
        let before = OUTSTANDING_FFI_STRINGS.load(Ordering::SeqCst);
        let bad = CString::new("{ not json").unwrap();
        let mut response: *mut c_char = std::ptr::null_mut();
        let _ = nova_ssh_sftp_list_directory(bad.as_ptr(), &mut response);
        if !response.is_null() {
            nova_ssh_string_free(response);
        }
        let after = OUTSTANDING_FFI_STRINGS.load(Ordering::SeqCst);
        assert_eq!(before, after, "every FFI-allocated string must be freed");
    }
}
```

> Tests run single-threaded for this module if other tests also allocate FFI strings concurrently; if flakiness appears, gate with a serial guard or compare a local delta around the call rather than absolute counts. Prefer the before/after delta as written.

- [ ] **Step 3: Run**

Run: `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml alloc_balance`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs
git commit -m "feat(ssh-ffi): debug FFI-string alloc counter + balance test (#121)"
```
(+ Co-Authored-By trailer.)

---

## Part 3 — C# `NovaSshSafeHandle` + migration (item 2)

### Task 5: `NovaSshSafeHandle` + NativeMethods signature changes

**Files:**
- Create: `src/NovaTerminal.Platform/Ssh/Native/NativeSshSafeHandle.cs`
- Modify: `src/NovaTerminal.Platform/Ssh/Native/NativeSshInterop.cs` (the nested `NativeMethods` class, 915-959)

- [ ] **Step 1: Create `NovaSshSafeHandle`**

```csharp
using System;
using Microsoft.Win32.SafeHandles;

namespace NovaTerminal.Platform.Ssh.Native;

// Owns a native SSH session registry id (returned by nova_ssh_connect). Passing
// this to every session P/Invoke makes the marshaller AddRef before / Release
// after each call, so nova_ssh_close (ReleaseHandle) cannot run while a poll or
// write is in flight — closing the poll-vs-close use-after-free (#121/#118).
public sealed class NovaSshSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NovaSshSafeHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle()
    {
        NativeSshInterop.NativeMethods.nova_ssh_close_raw(handle);
        return true;
    }
}
```

- [ ] **Step 2: Update `NativeMethods` signatures**

In `NativeSshInterop.NativeMethods`, change the persistent-session imports to take `NovaSshSafeHandle`, make `nova_ssh_connect` return it, and add a raw close for `ReleaseHandle`. Also make `NativeMethods` `internal` (it's `private static class` today — promote to `internal static class` so `NovaSshSafeHandle` can call it; keep it nested in `NativeSshInterop`).

```csharp
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_connect")]
        public static extern NovaSshSafeHandle nova_ssh_connect(in NativeConnectArgs args);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_poll_event")]
        public static extern int nova_ssh_poll_event(NovaSshSafeHandle session, out NativeEventHeader @event, byte[] payload, nuint payloadCapacity);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_write")]
        public static extern int nova_ssh_write(NovaSshSafeHandle session, byte[] data, nuint dataLength);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_resize")]
        public static extern int nova_ssh_resize(NovaSshSafeHandle session, ushort cols, ushort rows);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_open_direct_tcpip")]
        public static extern int nova_ssh_open_direct_tcpip(NovaSshSafeHandle session, in NativeDirectTcpIpOpenArgs args);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_channel_write")]
        public static extern int nova_ssh_channel_write(NovaSshSafeHandle session, uint channelId, byte[] data, nuint dataLength);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_channel_eof")]
        public static extern int nova_ssh_channel_eof(NovaSshSafeHandle session, uint channelId);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_channel_close")]
        public static extern int nova_ssh_channel_close(NovaSshSafeHandle session, uint channelId);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_submit_response")]
        public static extern int nova_ssh_submit_response(NovaSshSafeHandle session, uint responseKind, byte[] data, nuint dataLength);

        // Raw close used only by NovaSshSafeHandle.ReleaseHandle().
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nova_ssh_close")]
        public static extern int nova_ssh_close_raw(IntPtr session);
```

(Leave `nova_ssh_sftp_transfer`, `nova_ssh_sftp_list_directory`, `nova_ssh_string_free` unchanged — they don't take a session handle.)

- [ ] **Step 3: Build (expect interface/impl errors — fixed in Task 6)**

Run: `scripts/build.ps1 build src/NovaTerminal.Platform`
Expected: FAIL — `INativeSshInterop`/`NativeSshInterop`/`NativeSshSession` still use `IntPtr`. Resolved in Task 6. (Proceed to Task 6 to reach green.)

---

### Task 6: Migrate interface, interop bodies, session, and port-forward

**Files:**
- Modify: `src/NovaTerminal.Platform/Ssh/Native/INativeSshInterop.cs`
- Modify: `src/NovaTerminal.Platform/Ssh/Native/NativeSshInterop.cs` (`Connect` + session methods)
- Modify: `src/NovaTerminal.Platform/Ssh/Sessions/NativeSshSession.cs`
- Modify: `src/NovaTerminal.Platform/Ssh/Native/NativePortForwardSession.cs`

- [ ] **Step 1: Migrate the interface**

In `INativeSshInterop.cs`, change `IntPtr` → `NovaSshSafeHandle` for the session methods and `Connect`'s return:

```csharp
    NovaSshSafeHandle Connect(NativeSshConnectionOptions options);
    NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle);
    void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data);
    void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows);
    int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options);
    void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data);
    void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId);
    void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId);
    void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data);
    void Close(NovaSshSafeHandle sessionHandle);
```

(`ListRemoteDirectory` and `RunSftpTransfer` are unchanged.)

- [ ] **Step 2: Migrate `NativeSshInterop` method bodies**

`Connect` (23-122): change the return type to `NovaSshSafeHandle`; replace the handle check + return:

```csharp
                NovaSshSafeHandle handle = NativeMethods.nova_ssh_connect(in args);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new InvalidOperationException("Failed to create native SSH session.");
                }

                return handle;
```

For each session method, change the parameter type to `NovaSshSafeHandle` and the guard from `== IntPtr.Zero` to `is null or { IsInvalid: true } or { IsClosed: true }`, then pass `sessionHandle` straight to the (now SafeHandle-typed) `NativeMethods` call. Example — `PollEvent`:

```csharp
    public NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle)
    {
        if (sessionHandle is null || sessionHandle.IsInvalid || sessionHandle.IsClosed)
        {
            return null;
        }
        // ... unchanged loop, but the nova_ssh_poll_event call now takes sessionHandle directly ...
    }
```

Apply the same parameter-type + guard change to `Write`, `Resize`, `OpenDirectTcpIp`, `WriteChannel`, `SendChannelEof`, `CloseChannel`, `SubmitResponse`, `Close`. For `Close`:

```csharp
    public void Close(NovaSshSafeHandle sessionHandle)
    {
        // Disposing the SafeHandle runs ReleaseHandle -> nova_ssh_close exactly once,
        // after any in-flight call's AddRef has been released.
        sessionHandle?.Dispose();
    }
```

(The old `Close` returned-code handling is no longer needed; `ReleaseHandle` ignores the return per SafeHandle contract.) Wrap each native call's marshalling concern is handled by the SafeHandle; no try/catch needed beyond the existing result-code checks. Note: a method racing a dispose can throw `ObjectDisposedException` from the marshaller — guard the bodies that loop/poll (`PollEvent`) with a `try { } catch (ObjectDisposedException) { return null; }` and the void methods with `catch (ObjectDisposedException) { }`.

- [ ] **Step 3: Migrate `NativeSshSession`**

- Change `private IntPtr _sessionHandle;` → `private NovaSshSafeHandle? _sessionHandle;`.
- `_sessionHandle = _interop.Connect(connectionOptions);` is unchanged (now returns the SafeHandle).
- Guards `_sessionHandle == IntPtr.Zero` → `_sessionHandle is null or { IsInvalid: true } or { IsClosed: true }`.
- The teardown (527-530): replace
  ```csharp
  IntPtr handle = Interlocked.Exchange(ref _sessionHandle, IntPtr.Zero);
  if (handle != IntPtr.Zero) { _interop.Close(handle); }
  ```
  with
  ```csharp
  NovaSshSafeHandle? handle = Interlocked.Exchange(ref _sessionHandle, null);
  handle?.Dispose();
  ```
- Pass `_sessionHandle` to `NativePortForwardSession` (83) — now a `NovaSshSafeHandle`.

- [ ] **Step 4: Migrate `NativePortForwardSession`**

Change its stored handle field and constructor parameter from `IntPtr` to `NovaSshSafeHandle`, and pass it through to `_interop.OpenDirectTcpIp/WriteChannel/SendChannelEof/CloseChannel`. It does **not** own/dispose the handle (the `NativeSshSession` does); it only borrows the reference for the session's lifetime.

- [ ] **Step 5: Build the native lib + the platform assembly**

Run: `cargo build --release --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml`
Then: `scripts/build.ps1 build src/NovaTerminal.Platform`
Expected: Build succeeded. Fix any remaining `IntPtr`-typed call sites the compiler flags.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.Platform/Ssh/Native/NativeSshSafeHandle.cs src/NovaTerminal.Platform/Ssh/Native/NativeSshInterop.cs src/NovaTerminal.Platform/Ssh/Native/INativeSshInterop.cs src/NovaTerminal.Platform/Ssh/Sessions/NativeSshSession.cs src/NovaTerminal.Platform/Ssh/Native/NativePortForwardSession.cs
git commit -m "feat(ssh): NovaSshSafeHandle ref-counts native SSH session FFI (#121 #118)"
```
(+ Co-Authored-By trailer.)

---

### Task 7: C# disposed-handle safety test

**Files:**
- Create: `tests/NovaTerminal.Platform.Tests/Ssh/NativeSshSafeHandleTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using NovaTerminal.Platform.Ssh.Native;
using Xunit;

namespace NovaTerminal.Platform.Tests.Ssh;

public class NativeSshSafeHandleTests
{
    [Fact]
    public void Dispose_IsIdempotent_AndDoesNotThrow()
    {
        // A default (invalid) handle should dispose safely and repeatedly.
        var handle = new NovaSshSafeHandle();
        Assert.True(handle.IsInvalid);

        var ex = Record.Exception(() =>
        {
            handle.Dispose();
            handle.Dispose();
        });

        Assert.Null(ex);
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void Interop_SessionMethods_NoOp_OnDisposedHandle()
    {
        var interop = new NativeSshInterop();
        var handle = new NovaSshSafeHandle();
        handle.Dispose();

        // Calling through a disposed/invalid handle must be a safe no-op, not a crash.
        var ex = Record.Exception(() =>
        {
            _ = interop.PollEvent(handle);
            interop.Write(handle, new byte[] { 1 });
            interop.Resize(handle, 80, 24);
            interop.Close(handle);
        });

        Assert.Null(ex);
    }
}
```

> A default `NovaSshSafeHandle` has handle value 0 (invalid), so `ReleaseHandle` is not invoked on dispose (SafeHandle skips release for invalid handles) — no native call occurs, making this test server-free and safe.

- [ ] **Step 2: Run**

Run: `scripts/build.ps1 test tests/NovaTerminal.Platform.Tests --filter "FullyQualifiedName~NativeSshSafeHandle"`
Expected: PASS (2 tests).

- [ ] **Step 3: Commit**

```bash
git add tests/NovaTerminal.Platform.Tests/Ssh/NativeSshSafeHandleTests.cs
git commit -m "test(ssh): NovaSshSafeHandle dispose idempotency + disposed-handle safety (#121)"
```
(+ Co-Authored-By trailer.)

---

## Part 4 — CI + final verification

### Task 8: Ensure `rusty_ssh` tests run in CI; final verification

**Files:**
- Modify (if needed): `.github/workflows/ci.yml`

- [ ] **Step 1: Check whether CI runs `rusty_ssh` cargo tests**

Search `ci.yml` for `rusty_ssh` and `cargo test`. The repo already has an `Analyze (rust)` job and builds the native crates. If there is no `cargo test` step for `rusty_ssh`, add one to the Rust job:

```yaml
      - name: Rust SSH FFI tests
        run: cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml
```

If a `cargo test` already covers the workspace/crate, leave it; just confirm `ffi_contract` and the new unit tests are included.

- [ ] **Step 2: Commit (only if ci.yml changed)**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: run rusty_ssh cargo tests (FFI contract + abuse suite) (#121)"
```
(+ Co-Authored-By trailer.)

- [ ] **Step 3: Final consolidated verification**

Run, expecting all green:
- `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml` → all pass (layout, null, handle-abuse, JSON-abuse, alloc-balance).
- `cargo build --release --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml`
- `scripts/build.ps1 build src/NovaTerminal.App` (full app builds with the migrated platform assembly).
- `scripts/build.ps1 test tests/NovaTerminal.Platform.Tests --filter "FullyQualifiedName~NativeSshSafeHandle"` → pass.

- [ ] **Step 4: Confirm the issue scope**

This PR closes #121 items 2, 4, 3. Note in the PR that item 1 (credential zeroization) is sub-project B (separate PR), and that `NativeSshDockerE2eTests` (Docker-gated, `[SKIP]` off-CI) remain unaffected.

## Notes for the implementer

- Do NOT run the whole `NovaTerminal.Platform.Tests` project broadly if it pulls in slow/Docker-gated SSH E2E tests; use the `--filter` shown. The native SSH E2E tests are `[SKIP]` without Docker.
- The native `rusty_ssh` artifact must be rebuilt (`cargo build --release …`) before the C# side resolves the changed exports at runtime.
- Build only via `scripts/build.ps1` / `scripts/build.sh`.
- Keep all exports `ffi_guard`-wrapped; never let a `.lock().unwrap()` panic escape (use `unwrap_or_else(|p| p.into_inner())` as shown).
