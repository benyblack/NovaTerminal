# PTY Teardown Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add native `pty_cancel_read` + a ref-counting `PtySafeHandle` and an ordered, idempotent teardown so closing a PTY session never blocks on a stuck read and can never use-after-free, plus stop the leaking UI timers.

**Architecture:** A new Rust FFI export breaks the pty so an in-flight blocking read returns; `PtySafeHandle` routes every `pty_*` call through runtime ref-counting so `pty_close` cannot run during any in-flight call; `RustPtySession.Dispose` cancels → joins (bounded) → closes; `TerminalPane`/`TerminalView`/`MainWindow` get idempotent teardown and timer stops.

**Tech Stack:** Rust (`portable-pty`, `windows-sys`, `libc`), C# (.NET 10, `Microsoft.Win32.SafeHandles`), xUnit, Avalonia.

**Spec:** `docs/superpowers/specs/2026-06-10-pty-teardown-hardening-design.md`

**Build/test commands (from repo root):**
- Rust: `cargo test --manifest-path src/NovaTerminal.App/native/Cargo.toml`
- C# (targeted, avoids the Mode-B headless flake): `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "Category=PtySmoke"`
- C# build: `scripts/build.ps1 build src/NovaTerminal.App`

> **Spec deviation (flagged):** the spec planned to convert all 9 `Parser.On*` lambdas to named handlers. Implementation review found `InitializeSession` (TerminalPane.axaml.cs:1445) recreates the `Parser` on every (re)start, so those handlers GC with the old parser and do **not** accumulate. The handlers that actually accumulate across restarts are on the **reused `TermView`** (`OnResize`, `MetricsChanged`). Part 3 fixes those instead. Net effect on #102 (no restart-time handler accumulation) is the same, with a smaller, lower-risk diff.

---

## File Structure

| File | Responsibility | Tasks |
|---|---|---|
| `src/NovaTerminal.App/native/src/lib.rs` | `pty_cancel_read`; `h_pc`/`h_process` interior mutability | 1–3 |
| `src/NovaTerminal.Pty/RustPtySession.cs` | `PtySafeHandle`; migrate `Native.*`; ordered idempotent `Dispose` | 4–7 |
| `tests/NovaTerminal.App.Tests/PtyThreadLifecycleTests.cs` | C# teardown regression guards | 6 |
| `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs` | idempotent `Dispose`; reused-`TermView` handler idempotency | 8 |
| `src/NovaTerminal.App/Shell/TerminalView.cs` | stop `_metricsTimer` on detach | 9 |
| `src/NovaTerminal.App/MainWindow.axaml.cs` | stop `_recordingToastTimer` on close | 10 |

---

## Part 1 — Rust native (`lib.rs`)

### Task 1: Make `h_pc` / `h_process` interior-mutable

**Files:**
- Modify: `src/NovaTerminal.App/native/src/lib.rs` (struct ~188–197; construction ~344–351, 414–423; readers in `pty_resize` ~504–516, `pty_get_pid` ~545–552, `pty_close` ~573–585)

- [ ] **Step 1: Change the struct fields**

In `pub struct PtyState` change the two Windows handle fields from plain `Option` to `Mutex<Option<…>>`:

```rust
pub struct PtyState {
    pub reader: Mutex<Box<dyn Read + Send>>,
    pub writer: Mutex<Box<dyn Write + Send>>,
    #[cfg(windows)]
    pub h_pc: Mutex<Option<windows_sys::Win32::System::Console::HPCON>>,
    #[cfg(windows)]
    pub h_process: Mutex<Option<windows_sys::Win32::Foundation::HANDLE>>,
    pub master: Mutex<Option<Box<dyn portable_pty::MasterPty + Send>>>,
    pub child: Mutex<Option<Box<dyn portable_pty::Child + Send>>>,
}
```

- [ ] **Step 2: Update the two construction sites**

Passthrough branch (was `h_pc: Some(h_pc), h_process: Some(h_process)`):

```rust
                let state = PtyState {
                    reader: Mutex::new(reader),
                    writer: Mutex::new(writer),
                    h_pc: Mutex::new(Some(h_pc)),
                    h_process: Mutex::new(Some(h_process)),
                    master: Mutex::new(None),
                    child: Mutex::new(None),
                };
```

Portable branch (was `h_pc: None, h_process: None`):

```rust
    let state = PtyState {
        reader: Mutex::new(reader),
        writer: Mutex::new(writer),
        #[cfg(windows)]
        h_pc: Mutex::new(None),
        #[cfg(windows)]
        h_process: Mutex::new(None),
        master: Mutex::new(Some(pair.master)),
        child: Mutex::new(Some(child)),
    };
```

- [ ] **Step 3: Update the readers in `pty_resize`, `pty_get_pid`, `pty_close`**

`pty_resize` Windows block:

```rust
        #[cfg(windows)]
        {
            if let Ok(h_pc_opt) = state.h_pc.lock() {
                if let Some(h_pc) = *h_pc_opt {
                    let size = windows_sys::Win32::System::Console::COORD {
                        X: cols as i16,
                        Y: rows as i16,
                    };
                    unsafe {
                        windows_sys::Win32::System::Console::ResizePseudoConsole(h_pc, size);
                    }
                    return;
                }
            }
        }
```

`pty_get_pid` Windows block:

```rust
        #[cfg(windows)]
        {
            if let Ok(h_process_opt) = state.h_process.lock() {
                if let Some(h_process) = *h_process_opt {
                    unsafe {
                        return windows_sys::Win32::System::Threading::GetProcessId(h_process) as c_int;
                    }
                }
            }
        }
```

`pty_close` Windows block (take so it is idempotent vs. `pty_cancel_read`):

```rust
        #[cfg(windows)]
        {
            if let Ok(mut h_pc_opt) = state.h_pc.lock() {
                if let Some(h_pc) = h_pc_opt.take() {
                    unsafe {
                        windows_sys::Win32::System::Console::ClosePseudoConsole(h_pc);
                    }
                }
            }
            if let Ok(mut h_process_opt) = state.h_process.lock() {
                if let Some(h_process) = h_process_opt.take() {
                    unsafe {
                        windows_sys::Win32::Foundation::CloseHandle(h_process);
                    }
                }
            }
        }
```

- [ ] **Step 4: Build to verify it compiles**

Run: `cargo build --manifest-path src/NovaTerminal.App/native/Cargo.toml`
Expected: builds clean (on Linux the `#[cfg(windows)]` blocks are skipped — that is fine).

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/native/src/lib.rs
git commit -m "refactor(pty): make h_pc/h_process interior-mutable for cancel/close split (#118)"
```

---

### Task 2: Add `pty_cancel_read` with a Rust test

**Files:**
- Modify: `src/NovaTerminal.App/native/src/lib.rs` (new export after `pty_get_pid`; new test module at end)

- [ ] **Step 1: Write the failing Rust test**

Add at the end of `lib.rs`:

```rust
#[cfg(test)]
mod cancel_read_tests {
    use super::*;
    use std::ffi::CString;
    use std::time::Instant;

    // A blocking pty_read on a child with no output must return promptly once
    // pty_cancel_read is called (kill-to-EOF). Guards #119 / the Dispose join.
    #[test]
    fn cancel_read_unblocks_a_pending_read() {
        // A shell that just sleeps so it produces no output on its own.
        #[cfg(windows)]
        let (cmd, args) = ("cmd.exe", "/c timeout /t 30 /nobreak >NUL");
        #[cfg(not(windows))]
        let (cmd, args) = ("/bin/sh", "-c 'sleep 30'");

        let c_cmd = CString::new(cmd).unwrap();
        let c_args = CString::new(args).unwrap();
        let state = pty_spawn(
            c_cmd.as_ptr(),
            c_args.as_ptr(),
            std::ptr::null(),
            80,
            24,
        );
        assert!(!state.is_null(), "spawn failed");

        // Reader thread: a single blocking read that should return after cancel.
        let state_addr = state as usize;
        let reader = std::thread::spawn(move || {
            let ptr = state_addr as *mut PtyState;
            let mut buf = [0u8; 256];
            pty_read(ptr, buf.as_mut_ptr(), buf.len() as c_int)
        });

        // Give the read time to block, then cancel.
        std::thread::sleep(std::time::Duration::from_millis(300));
        let start = Instant::now();
        pty_cancel_read(state);

        // The read must return within a couple of seconds, not in ~30s.
        let rc = reader.join().expect("reader thread panicked");
        assert!(
            start.elapsed() < std::time::Duration::from_secs(5),
            "pty_read did not return promptly after cancel"
        );
        assert!(rc <= 0, "expected EOF(0) or error(-1) after cancel, got {rc}");

        // close must remain safe (idempotent vs the cancel that already ran).
        pty_close(state);
    }
}
```

- [ ] **Step 2: Run it to verify it fails to compile**

Run: `cargo test --manifest-path src/NovaTerminal.App/native/Cargo.toml cancel_read`
Expected: FAIL — `cannot find function pty_cancel_read in this scope`.

- [ ] **Step 3: Implement `pty_cancel_read`**

Add after `pty_get_pid` (before `pty_close`):

```rust
/// Unblock an in-flight `pty_read` so the caller's read thread can be joined.
///
/// The blocked read holds `state.reader`'s lock, so we must NEVER touch `reader`
/// here. Instead we break the pty from the other side: close the pseudoconsole
/// (Windows passthrough) and/or kill the child (portable path), which makes the
/// pending `read()` return EOF/error. Idempotent and safe to call before
/// `pty_close` (it `take()`s the HPCON so close won't double-free).
#[unsafe(no_mangle)]
pub extern "C" fn pty_cancel_read(state_ptr: *mut PtyState) {
    ffi_guard((), || {
        if state_ptr.is_null() {
            return;
        }
        // Clone the Arc without consuming the caller's ref (same idiom as pty_read).
        let state = unsafe {
            let arc = Arc::from_raw(state_ptr);
            let cloned = arc.clone();
            let _ = Arc::into_raw(arc);
            cloned
        };

        // Windows passthrough: closing the pseudoconsole breaks the output pipe so
        // the in-flight ReadFile on h_out_read returns. take() => pty_close won't
        // double-close.
        #[cfg(windows)]
        {
            if let Ok(mut h_pc_opt) = state.h_pc.lock() {
                if let Some(h_pc) = h_pc_opt.take() {
                    unsafe {
                        windows_sys::Win32::System::Console::ClosePseudoConsole(h_pc);
                    }
                }
            }
        }

        // Portable path (Unix + Windows non-passthrough): kill the child so the
        // slave closes and the master read returns EOF.
        if let Ok(mut child_opt) = state.child.lock() {
            if let Some(child) = child_opt.as_mut() {
                let _ = child.kill();
            }
        }
    })
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cargo test --manifest-path src/NovaTerminal.App/native/Cargo.toml cancel_read`
Expected: PASS.

- [ ] **Step 5: Run the full Rust test suite (no regressions)**

Run: `cargo test --manifest-path src/NovaTerminal.App/native/Cargo.toml`
Expected: all pass (`ffi_guard_tests`, `passthrough_decision_tests`, `cancel_read_tests`).

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/native/src/lib.rs
git commit -m "feat(pty): add pty_cancel_read to unblock a pending read (#119)"
```

---

### Task 3: Rebuild the native lib so C# picks it up

**Files:** none (build artifact)

- [ ] **Step 1: Build the native library in release**

Run: `cargo build --release --manifest-path src/NovaTerminal.App/native/Cargo.toml`
Expected: produces `rusty_pty` (`.dll`/`.so`) in the native `target/release`.

> Note: the `.csproj` copies the native artifact during the app build. If a task below reports a missing `pty_cancel_read` entrypoint at runtime, re-run this step and rebuild the app.

---

## Part 2 — `RustPtySession.cs`

### Task 4: Add `PtySafeHandle`

**Files:**
- Modify: `src/NovaTerminal.Pty/RustPtySession.cs` (add nested type + `using`)

- [ ] **Step 1: Add the using**

At the top, add:

```csharp
using Microsoft.Win32.SafeHandles;
```

- [ ] **Step 2: Add the `PtySafeHandle` nested class**

Inside the `RustPtySession` class (e.g. directly after the `Native` class), add:

```csharp
        // Owns the *mut PtyState returned by pty_spawn. Passing this to every
        // pty_* P/Invoke makes the marshaller AddRef before / Release after the
        // call, so pty_close (ReleaseHandle) can never run while a pty_read (or
        // any other call) is in flight — closing the #118 use-after-free window.
        internal sealed class PtySafeHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public PtySafeHandle() : base(ownsHandle: true) { }

            protected override bool ReleaseHandle()
            {
                Native.pty_close(handle);
                return true;
            }
        }
```

- [ ] **Step 3: Build to verify it compiles**

Run: `scripts/build.ps1 build src/NovaTerminal.Pty`
Expected: builds (warnings ok). `pty_close(IntPtr)` still exists in `Native`, so `ReleaseHandle` resolves.

- [ ] **Step 4: Commit**

```bash
git add src/NovaTerminal.Pty/RustPtySession.cs
git commit -m "feat(pty): add PtySafeHandle wrapping the native PTY pointer (#118)"
```

---

### Task 5: Migrate `Native.*` signatures and the `_ptyState` field to `PtySafeHandle`

**Files:**
- Modify: `src/NovaTerminal.Pty/RustPtySession.cs` (`Native` class 39–66; field 14; ctor 233/237/240–243; `IsProcessRunning` 35; `HasActiveChildProcesses` 75–78; `Pid` 86–88; `ReadLoop` 349/351; `SendInput` 431/436; `Resize` 441/445)

- [ ] **Step 1: Update the `Native` P/Invoke signatures**

`pty_spawn`/`pty_spawn_with_envs` return `PtySafeHandle`; `pty_close` keeps an `IntPtr` overload (used by `ReleaseHandle`); all other calls take `PtySafeHandle`; add `pty_cancel_read`:

```csharp
            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern PtySafeHandle pty_create(string cmd, ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern PtySafeHandle pty_spawn(string cmd, string? args, string? cwd, ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern PtySafeHandle pty_spawn_with_envs(string cmd, string? args, string? cwd, ushort cols, ushort rows, string envs);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_read(PtySafeHandle state, byte[] buffer, int len);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_write(PtySafeHandle state, byte[] buffer, int len);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void pty_resize(PtySafeHandle state, ushort cols, ushort rows);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int pty_get_pid(PtySafeHandle state);

            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void pty_cancel_read(PtySafeHandle state);

            // Raw overload used only by PtySafeHandle.ReleaseHandle().
            [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void pty_close(IntPtr state);
```

- [ ] **Step 2: Change the field**

Replace `private IntPtr _ptyState;` (line 14) with:

```csharp
        private readonly PtySafeHandle _handle;
```

- [ ] **Step 3: Update the constructor assignment + null check**

Replace the spawn/assignment block (233–243). The two spawns now return `PtySafeHandle`:

```csharp
                _handle = Native.pty_spawn_with_envs(effectiveShell, combinedArgs.Trim(), cwd, (ushort)cols, (ushort)rows, sb.ToString());
            }
            else
            {
                _handle = Native.pty_spawn(effectiveShell, combinedArgs.Trim(), cwd, (ushort)cols, (ushort)rows);
            }

            if (_handle.IsInvalid)
            {
                throw new InvalidOperationException("Failed to create Rust PTY session.");
            }
```

- [ ] **Step 4: Update the readers of `_ptyState`**

`IsProcessRunning` (35):

```csharp
        public bool IsProcessRunning => Volatile.Read(ref _isExited) == 0 && !_handle.IsInvalid;
```

`HasActiveChildProcesses` (75–78):

```csharp
                if (_handle.IsInvalid) return false;
                int pid = Native.pty_get_pid(_handle);
                if (pid <= 0) return false;
                return HasChildProcesses(pid, ShellCommand);
```

`Pid` (86–88):

```csharp
                if (_handle.IsInvalid) return null;
                int pid = Native.pty_get_pid(_handle);
                return pid > 0 ? pid : null;
```

`ReadLoop` (349, 351):

```csharp
                while (!_cts.Token.IsCancellationRequested && !_handle.IsInvalid)
                {
                    int read = Native.pty_read(_handle, buffer, buffer.Length);
```

`SendInput` (431, 436):

```csharp
            if (_handle.IsInvalid) return;

            _recorder?.RecordInput(input);

            byte[] data = Encoding.UTF8.GetBytes(input);
            Native.pty_write(_handle, data, data.Length);
```

`Resize` (441, 445):

```csharp
            if (_handle.IsInvalid || cols <= 0 || rows <= 0) return;
            _cols = cols;
            _rows = rows;
            Console.WriteLine($"[RustPtySession] Resizing to {cols}x{rows}");
            Native.pty_resize(_handle, (ushort)cols, (ushort)rows);
```

- [ ] **Step 5: Guard `ReadLoop` against a disposed handle race**

A marshalled `pty_read(_handle, …)` throws `ObjectDisposedException` if `Dispose` closed `_handle` between the `IsInvalid` check and the call. Treat it as shutdown. In `ReadLoop`'s outer `catch (Exception ex)` block, add a specific catch *before* it:

```csharp
            catch (ObjectDisposedException)
            {
                // _handle was disposed by Dispose() — normal shutdown.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RustPtySession] ReadLoop terminated by unhandled exception: {ex}");
            }
```

- [ ] **Step 6: Build to verify it compiles (Dispose still references old `_ptyState` — expected fail)**

Run: `scripts/build.ps1 build src/NovaTerminal.Pty`
Expected: FAIL — `Dispose()` still uses `_ptyState`/`Native.pty_close(_ptyState)`. Fixed in Task 6. (If you prefer a green build here, do Task 6 Step 3 now.)

---

### Task 6: Ordered, idempotent `Dispose` + C# regression tests

**Files:**
- Modify: `src/NovaTerminal.Pty/RustPtySession.cs` (`Dispose` 449–474; add fields/const)
- Test: `tests/NovaTerminal.App.Tests/PtyThreadLifecycleTests.cs`

- [ ] **Step 1: Write the failing tests**

Add these to `PtyThreadLifecycleTests`:

```csharp
        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task Dispose_WhileReadBlocked_JoinsLoopThreadsPromptly()
        {
            string shell = ShellHelper.GetDefaultShell();
            var session = new RustPtySession(shell, 80, 24);

            await WaitUntilAsync(
                () => session.ReadLoopThread is not null && session.ProcessLoopThread is not null,
                TimeSpan.FromSeconds(5));

            Thread read = session.ReadLoopThread!;
            Thread process = session.ProcessLoopThread!;

            // Let the shell go idle (read parked in native pty_read).
            await Task.Delay(500);

            var sw = Stopwatch.StartNew();
            session.Dispose();
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                $"Dispose took {sw.Elapsed.TotalSeconds:F1}s — cancel/join did not unblock the read");
            Assert.False(read.IsAlive, "ReadLoop thread should have exited after Dispose");
            Assert.False(process.IsAlive, "ProcessLoop thread should have exited after Dispose");
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public void Dispose_IsIdempotent()
        {
            string shell = ShellHelper.GetDefaultShell();
            var session = new RustPtySession(shell, 80, 24);

            var ex = Record.Exception(() =>
            {
                session.Dispose();
                session.Dispose();
                session.Dispose();
            });

            Assert.Null(ex);
        }
```

- [ ] **Step 2: Run to verify they fail**

Run: `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "Category=PtySmoke"`
Expected: build FAIL (the old `Dispose` still references `_ptyState`) — or, if Task 5 left it red, that compile error.

- [ ] **Step 3: Add fields/const and rewrite `Dispose`**

Add near the other fields:

```csharp
        private static readonly TimeSpan DisposeJoinTimeout = TimeSpan.FromSeconds(2);
        private int _disposed;
```

Replace `Dispose()` (449–474) with:

```csharp
        public void Dispose()
        {
            // Idempotent: only the first caller runs teardown.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            if (_recorder != null)
            {
                try
                {
                    StopRecording();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RustPtySession] StopRecording during dispose failed: {ex.Message}");
                }
            }

            // 1. Stop the loops re-entering native calls.
            _cts.Cancel();

            // 2. Unblock the in-flight native read so the read thread can exit.
            if (!_handle.IsInvalid)
            {
                try { Native.pty_cancel_read(_handle); }
                catch (ObjectDisposedException) { /* already gone */ }
            }

            // 3. Let the process loop drain and exit.
            if (!_outputQueue.IsAddingCompleted)
            {
                _outputQueue.CompleteAdding();
            }

            // 4. Join (bounded) so there are no late callbacks and no thread inside a pty_* call.
            if (!(_readLoopThread?.Join(DisposeJoinTimeout) ?? true))
            {
                Console.WriteLine("[RustPtySession] ReadLoop did not exit within join timeout.");
            }
            if (!(_processLoopThread?.Join(DisposeJoinTimeout) ?? true))
            {
                Console.WriteLine("[RustPtySession] ProcessLoop did not exit within join timeout.");
            }

            // 5. Release the handle. SafeHandle guarantees pty_close runs only once
            //    no pty_* call is in flight, so this is UAF-safe even if a join timed out.
            _handle.Dispose();

            TryNotifyExit(0);
        }
```

- [ ] **Step 4: Build the native lib + app dependency, then run the tests**

Run: `cargo build --release --manifest-path src/NovaTerminal.App/native/Cargo.toml`
Then: `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "Category=PtySmoke"`
Expected: PASS — all `PtySmoke` tests green (existing thread test + the two new ones).

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.Pty/RustPtySession.cs tests/NovaTerminal.App.Tests/PtyThreadLifecycleTests.cs
git commit -m "feat(pty): ordered idempotent Dispose (cancel->join->close) (#119 #118 #103)"
```

---

### Task 7: Full app build sanity

**Files:** none

- [ ] **Step 1: Build the app**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: builds clean. Fix any remaining `_ptyState` references the compiler flags (there should be none).

- [ ] **Step 2: Commit (only if fixes were needed)**

```bash
git add -A
git commit -m "fix(pty): finish PtySafeHandle migration call sites"
```

---

## Part 3 — `TerminalPane` (#103 pane side + targeted #102)

### Task 8: Idempotent pane `Dispose` + reused-`TermView` handler idempotency

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs` (fields near 96; `InitializeSession` resize/metrics subs 1629–1648; `Dispose` 2183–2201)

- [ ] **Step 1: Add a disposed guard field and named-handler fields**

Near the other private fields (~96):

```csharp
        private bool _disposed;
        private Action<int, int>? _onTermViewResize;
        private Action<float, float>? _onTermViewMetricsChanged;
```

> Types mirror the event declarations: `TermView.OnResize` is `Action<int, int>` (TerminalView.cs:1212) and `TermView.MetricsChanged` is `Action<float, float>` (TerminalView.cs:49).

- [ ] **Step 2: Make the `TermView` resize/metrics subscriptions idempotent**

Replace the inline lambda subscriptions at 1629–1648 with detach-before-attach using stored delegates (the established codebase idiom, e.g. MainWindow.axaml.cs:2532). First assign the fields once, then detach+attach:

```csharp
            // Wire up Resize (idempotent across InitializeSession re-runs — TermView is reused).
            _onTermViewResize ??= (c, r) =>
            {
                if (Parser != null)
                {
                    float cwResize = TermView.Metrics.CellWidth;
                    float chResize = TermView.Metrics.CellHeight;
                    if (cwResize > 0) Parser.CellWidth = cwResize;
                    if (chResize > 0) Parser.CellHeight = chResize;
                }
                Session?.Resize(c, r);
            };
            TermView.OnResize -= _onTermViewResize;
            TermView.OnResize += _onTermViewResize;

            // Metrics changed handling (idempotent — TermView is reused).
            _onTermViewMetricsChanged ??= (cwMetric, chMetric) =>
            {
                if (Parser != null && cwMetric > 0 && chMetric > 0)
                {
                    Parser.CellWidth = cwMetric;
                    Parser.CellHeight = chMetric;
                }
            };
            TermView.MetricsChanged -= _onTermViewMetricsChanged;
            TermView.MetricsChanged += _onTermViewMetricsChanged;
```

> The lambdas reference the `Parser`/`Session` fields (not locals), so storing them once and reusing across restarts is correct — they always act on the current parser/session.

- [ ] **Step 3: Make `Dispose` idempotent and detach the reused handlers**

Replace `Dispose` (2183–2201) with:

```csharp
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CloseRemoteFilesSidebar();

            // Detach handlers on the reused TermView so a disposed pane stops reacting.
            if (_onTermViewResize != null) TermView.OnResize -= _onTermViewResize;
            if (_onTermViewMetricsChanged != null) TermView.MetricsChanged -= _onTermViewMetricsChanged;

            if (Buffer != null)
            {
                Buffer.OnScreenSwitched -= OnBufferScreenSwitched;
            }
            _statusTimer?.Stop();
            _statusTimer = null;
            SftpService.Instance.JobUpdated -= Sftp_JobUpdated;
            if (Session != null)
            {
                ITerminalSession session = Session;
                UnregisterActiveSshSession(session);
                Session = null;
                session.Dispose();
            }
        }
```

- [ ] **Step 4: Build to verify it compiles**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: builds clean.

- [ ] **Step 5: Manual smoke verification** (headless UI tests hit the Mode-B flake — verify by hand per the project's GUI smoke-test preference)

Run the app (`scripts/build.ps1` run target or launch the built exe), then:
1. Open a tab, type to confirm output works.
2. Resize the window several times — confirm no duplicated/janky resize behavior.
3. Trigger a reconnect/restart (SSH disconnect → Enter, or the reconnect path) a few times — confirm a single bell/title per event and the old session's CPU drops (no lingering process).
4. Close tabs/panes repeatedly — confirm the app stays responsive and no orphan shells accumulate (Task Manager / `pgrep`).

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml.cs
git commit -m "fix(pane): idempotent Dispose + idempotent TermView handlers across restart (#103 #102)"
```

---

## Part 4 — Leaking timers (targeted #102)

### Task 9: Stop `_metricsTimer` on detach in `TerminalView`

**Files:**
- Modify: `src/NovaTerminal.App/Shell/TerminalView.cs` (`OnAttachedToVisualTree` 761–782; `OnDetachedFromVisualTree` 784–796)

- [ ] **Step 1: Stop `_metricsTimer` on detach**

In `OnDetachedFromVisualTree`, after `StopUiTimers();` (789), add:

```csharp
            _metricsTimer.Stop();
```

- [ ] **Step 2: Restart `_metricsTimer` on (re)attach so behavior is preserved**

In `OnAttachedToVisualTree`, after `RefreshUiTimerState();` (765), add:

```csharp
            if (!_metricsTimer.IsEnabled) _metricsTimer.Start();
```

> Rationale: `_metricsTimer` was started in the ctor and never stopped, so it kept firing every 5s after detach, rooting the view via its `Tick` delegate. Stopping on detach removes the leak; restarting on attach preserves metrics while the view is live.

- [ ] **Step 3: Build to verify it compiles**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: builds clean.

- [ ] **Step 4: Commit**

```bash
git add src/NovaTerminal.App/Shell/TerminalView.cs
git commit -m "fix(view): stop _metricsTimer on detach to drop the per-view leak (#102)"
```

---

### Task 10: Stop `_recordingToastTimer` on close in `MainWindow`

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (`OnClosing` 5031–5040)

- [ ] **Step 1: Stop the toast timer in `OnClosing`**

In `OnClosing`, before `_globalHotkey?.Dispose();` (5039), add:

```csharp
            _recordingToastTimer.Stop();
```

- [ ] **Step 2: Build to verify it compiles**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: builds clean.

- [ ] **Step 3: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs
git commit -m "fix(window): stop _recordingToastTimer in OnClosing (#102)"
```

---

## Final verification

- [ ] **Rust:** `cargo test --manifest-path src/NovaTerminal.App/native/Cargo.toml` → all pass.
- [ ] **Native rebuilt:** `cargo build --release --manifest-path src/NovaTerminal.App/native/Cargo.toml`.
- [ ] **C# contract tests:** `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "Category=PtySmoke"` → all pass.
- [ ] **App builds:** `scripts/build.ps1 build src/NovaTerminal.App` → clean.
- [ ] **Manual smoke** (Task 8 Step 5) performed.
- [ ] **Issues:** confirm the PR closes #119, #118, #103, and the targeted part of #102. Note in the PR that the `rusty_ssh` SafeHandle mirror and full-`CompositeDisposable` adoption are intentionally out of scope (follow-ups), and that Mode-B re-gating remains tracked in #117.

## Notes for the implementer

- **Do not** run the whole `NovaTerminal.App.Tests` project or full-solution `dotnet test` — the headless Avalonia lane hits the upstream Mode-B deadlock (#81/#117). Always filter to `Category=PtySmoke` for the contract tests, which are non-headless.
- Always build via `scripts/build.ps1` / `scripts/build.sh`, never raw `dotnet build` (it hangs when stdout is piped — see CLAUDE.md).
- The native artifact must be rebuilt (Task 3) before the C# side can resolve `pty_cancel_read` at runtime.
