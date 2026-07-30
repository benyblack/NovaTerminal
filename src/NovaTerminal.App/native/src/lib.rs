use libc::{c_char, c_int};
use portable_pty::{CommandBuilder, NativePtySystem, PtySize, PtySystem};
use std::ffi::CStr;
use std::io::{Read, Write};
use std::panic::{catch_unwind, AssertUnwindSafe};
#[cfg(windows)]
use std::sync::atomic::{AtomicU32, Ordering};

#[cfg(windows)]
mod win32 {
    use super::*;
    use std::os::windows::io::FromRawHandle;
    use std::ptr::{null, null_mut};
    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::System::Console::{
        ClosePseudoConsole, CreatePseudoConsole, GetConsoleWindow, COORD, HPCON,
    };
    use windows_sys::Win32::System::Pipes::CreatePipe;
    use windows_sys::Win32::System::Threading::{
        CreateProcessW, DeleteProcThreadAttributeList, InitializeProcThreadAttributeList,
        UpdateProcThreadAttribute, EXTENDED_STARTUPINFO_PRESENT, LPPROC_THREAD_ATTRIBUTE_LIST,
        PROCESS_INFORMATION, STARTUPINFOEXW,
    };

    pub const PSEUDOCONSOLE_PASSTHROUGH: u32 = 0x8;
    pub const PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE: usize = 0x00020016;

    /// True when this process is attached to a real console window. A GUI (WinExe)
    /// app returns false even when launched from a terminal, since it never allocates
    /// a console. Used to avoid the PSEUDOCONSOLE_PASSTHROUGH path, which drops child
    /// stdout when no real console is present.
    pub fn host_has_real_console() -> bool {
        (unsafe { GetConsoleWindow() }) != 0
    }

    /// Owns a Win32 handle and closes it on drop.
    ///
    /// `spawn_with_passthrough` has five failure exits between creating its first pipe and
    /// handing the surviving handles to the caller. Each one used to `return Err(..)` without
    /// closing whatever it had already created, so a shell that failed to launch leaked two to
    /// four kernel handles per attempt (#120 item 4). Making ownership explicit removes the whole
    /// class of mistake instead of patching the exits one at a time — the compiler now enforces
    /// that a handle is either dropped or deliberately released.
    struct OwnedHandle(HANDLE);

    impl OwnedHandle {
        fn get(&self) -> HANDLE {
            self.0
        }

        /// Gives up ownership. The returned handle is the caller's to close.
        fn release(mut self) -> HANDLE {
            let handle = self.0;
            self.0 = INVALID_HANDLE_VALUE;
            handle
        }
    }

    impl Drop for OwnedHandle {
        fn drop(&mut self) {
            // 0 as well as INVALID_HANDLE_VALUE: windows-sys models HANDLE as an integer, and a
            // zeroed struct field is a plausible source of 0 here.
            if self.0 != INVALID_HANDLE_VALUE && self.0 != 0 {
                unsafe { CloseHandle(self.0) };
            }
        }
    }

    /// Owns an HPCON and closes it on drop. Same reasoning as [`OwnedHandle`]: on the
    /// `CreateProcessW` failure path this was closed by hand, and on the two exits before it the
    /// pseudoconsole did not exist yet — but relying on that reading each time an exit is added is
    /// how the leaks appeared in the first place.
    struct OwnedPseudoConsole(HPCON);

    impl OwnedPseudoConsole {
        fn get(&self) -> HPCON {
            self.0
        }

        fn release(mut self) -> HPCON {
            let handle = self.0;
            self.0 = 0;
            handle
        }
    }

    impl Drop for OwnedPseudoConsole {
        fn drop(&mut self) {
            if self.0 != 0 {
                unsafe { ClosePseudoConsole(self.0) };
            }
        }
    }

    /// An initialized PROC_THREAD_ATTRIBUTE_LIST plus its backing buffer.
    ///
    /// `DeleteProcThreadAttributeList` releases allocations the list makes internally; freeing the
    /// buffer alone is not enough. It was called only on the `CreateProcessW` failure path, so
    /// every *successful* spawn leaked those internals — the opposite of the usual pattern, and
    /// easy to miss because the buffer itself is a `Vec` that does get dropped.
    struct OwnedAttributeList {
        buffer: Vec<u8>,
    }

    impl OwnedAttributeList {
        /// Sizes, allocates and initializes a list with room for `count` attributes.
        fn with_capacity(count: u32) -> Result<Self, anyhow::Error> {
            unsafe {
                let mut size: usize = 0;
                // The sizing call is *expected* to fail with ERROR_INSUFFICIENT_BUFFER; only the
                // size it writes back is meaningful, so check that rather than the return value.
                InitializeProcThreadAttributeList(null_mut(), count, 0, &mut size);
                if size == 0 {
                    return Err(anyhow::anyhow!(
                        "InitializeProcThreadAttributeList returned a zero size: {}",
                        std::io::Error::last_os_error()
                    ));
                }

                let mut buffer = vec![0u8; size];
                if InitializeProcThreadAttributeList(
                    buffer.as_mut_ptr() as LPPROC_THREAD_ATTRIBUTE_LIST,
                    count,
                    0,
                    &mut size,
                ) == 0
                {
                    return Err(anyhow::anyhow!(
                        "InitializeProcThreadAttributeList failed: {}",
                        std::io::Error::last_os_error()
                    ));
                }

                Ok(Self { buffer })
            }
        }

        fn as_ptr(&mut self) -> LPPROC_THREAD_ATTRIBUTE_LIST {
            self.buffer.as_mut_ptr() as LPPROC_THREAD_ATTRIBUTE_LIST
        }
    }

    impl Drop for OwnedAttributeList {
        fn drop(&mut self) {
            unsafe {
                DeleteProcThreadAttributeList(
                    self.buffer.as_mut_ptr() as LPPROC_THREAD_ATTRIBUTE_LIST
                )
            };
        }
    }

    pub fn spawn_with_passthrough(
        cmd: &str,
        args: Option<&str>,
        cwd: Option<&str>,
        cols: u16,
        rows: u16,
        extra_envs: &[(String, String)],
    ) -> Result<(Box<dyn Read + Send>, Box<dyn Write + Send>, HPCON, HANDLE), anyhow::Error> {
        unsafe {
            let mut h_in_read: HANDLE = INVALID_HANDLE_VALUE;
            let mut h_in_write: HANDLE = INVALID_HANDLE_VALUE;
            let mut h_out_read: HANDLE = INVALID_HANDLE_VALUE;
            let mut h_out_write: HANDLE = INVALID_HANDLE_VALUE;

            if CreatePipe(&mut h_in_read, &mut h_in_write, null_mut(), 0) == 0 {
                return Err(anyhow::anyhow!(
                    "Failed to create input pipe: {}",
                    std::io::Error::last_os_error()
                ));
            }
            // Take ownership immediately, before the next call that can fail. Everything from here
            // to the final `release()` calls is leak-free on any early return (#120 item 4).
            let in_read = OwnedHandle(h_in_read);
            let in_write = OwnedHandle(h_in_write);

            if CreatePipe(&mut h_out_read, &mut h_out_write, null_mut(), 0) == 0 {
                return Err(anyhow::anyhow!(
                    "Failed to create output pipe: {}",
                    std::io::Error::last_os_error()
                ));
            }
            let out_read = OwnedHandle(h_out_read);
            let out_write = OwnedHandle(h_out_write);

            let size = COORD {
                X: cols as i16,
                Y: rows as i16,
            };
            let mut h_pc: HPCON = 0;
            let res = CreatePseudoConsole(
                size,
                in_read.get(),
                out_write.get(),
                PSEUDOCONSOLE_PASSTHROUGH,
                &mut h_pc,
            );
            if res != 0 {
                return Err(anyhow::anyhow!("CreatePseudoConsole failed: {:x}", res));
            }
            let pseudo_console = OwnedPseudoConsole(h_pc);

            // The pseudoconsole duplicated what it needs; our ends of those two pipes are done.
            // Dropping the guards closes them, which is what the explicit CloseHandle calls here
            // used to do.
            drop(in_read);
            drop(out_write);

            let mut si_ex: STARTUPINFOEXW = std::mem::zeroed();
            si_ex.StartupInfo.cb = std::mem::size_of::<STARTUPINFOEXW>() as u32;

            let mut attr_list = OwnedAttributeList::with_capacity(1)?;
            let lp_attr_list = attr_list.as_ptr();

            // A failure here means the child would launch with no pseudoconsole attached: it would
            // inherit this process's console (or none at all), so its output would never reach our
            // pipes. Previously unchecked, which turned that into a silently blank tab.
            if UpdateProcThreadAttribute(
                lp_attr_list,
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                pseudo_console.get() as *const _,
                std::mem::size_of::<HPCON>(),
                null_mut(),
                null_mut(),
            ) == 0
            {
                return Err(anyhow::anyhow!(
                    "UpdateProcThreadAttribute(PSEUDOCONSOLE) failed: {}",
                    std::io::Error::last_os_error()
                ));
            }
            si_ex.lpAttributeList = lp_attr_list;

            let mut pi: PROCESS_INFORMATION = std::mem::zeroed();
            // Quote the executable when its path contains whitespace and
            // isn't already quoted. CreateProcessW with lpApplicationName
            // NULL uses heuristics to find the exe boundary in lpCommandLine
            // -- for `C:\Program Files\PowerShell\7\pwsh.exe -NoLogo ...`
            // it first tries `C:\Program.exe` and only falls back to the
            // longer prefix if that fails, which breaks for many users.
            // Wrapping the exe in quotes removes the ambiguity.
            let needs_quoting = cmd.contains(char::is_whitespace) && !cmd.starts_with('"');
            let mut full_cmd = if needs_quoting {
                format!("\"{}\"", cmd)
            } else {
                cmd.to_string()
            };
            if let Some(a) = args {
                full_cmd.push(' ');
                full_cmd.push_str(a);
            }
            let mut cmd_utf16: Vec<u16> = full_cmd.encode_utf16().chain(Some(0)).collect();
            let cwd_utf16: Option<Vec<u16>> =
                cwd.map(|s| s.encode_utf16().chain(Some(0)).collect());

            // Prepare environment block.
            // TERM keeps xterm compatibility while COLORTERM=truecolor allows apps
            // (e.g. chafa/superfile) to select full RGB output instead of dithered
            // 16/256-color fallback blocks.
            let mut env_map: std::collections::HashMap<String, String> = std::env::vars().collect();
            env_map.insert("TERM".to_string(), "xterm-256color".to_string());
            env_map.insert("COLORTERM".to_string(), "truecolor".to_string());
            env_map.insert("TERM_PROGRAM".to_string(), "NovaTerminal".to_string());
            // Caller-supplied overrides take precedence so shell-integration
            // providers (e.g. zsh's ZDOTDIR) can steer shell startup.
            for (k, v) in extra_envs {
                env_map.insert(k.clone(), v.clone());
            }

            let mut env_block: Vec<u16> = Vec::new();
            for (key, value) in env_map {
                let entry = format!("{}={}\0", key, value);
                env_block.extend(entry.encode_utf16());
            }
            env_block.push(0); // Final double null terminator

            let created = CreateProcessW(
                null(),
                cmd_utf16.as_mut_ptr(),
                null_mut(),
                null_mut(),
                0,
                EXTENDED_STARTUPINFO_PRESENT
                    | windows_sys::Win32::System::Threading::CREATE_UNICODE_ENVIRONMENT,
                env_block.as_mut_ptr() as *mut _,
                cwd_utf16.as_ref().map_or(null(), |v| v.as_ptr()),
                &si_ex.StartupInfo,
                &mut pi,
            );

            if created == 0 {
                // No manual cleanup: pseudo_console, attr_list, out_read and in_write all drop on
                // the way out. The two pipe handles were leaking here before.
                return Err(anyhow::anyhow!(
                    "CreateProcessW failed: {}",
                    std::io::Error::last_os_error()
                ));
            }

            CloseHandle(pi.hThread);

            // The attribute list has done its job; dropping it calls DeleteProcThreadAttributeList,
            // which the success path never did.
            drop(attr_list);

            // Ownership of these three moves to the caller (File takes the raw handles, and
            // PtyState closes the HPCON), so release them from the guards rather than dropping.
            let reader = std::fs::File::from_raw_handle(out_read.release() as _);
            let writer = std::fs::File::from_raw_handle(in_write.release() as _);

            Ok((
                Box::new(reader) as Box<dyn Read + Send>,
                Box::new(writer) as Box<dyn Write + Send>,
                pseudo_console.release(),
                pi.hProcess,
            ))
        }
    }

    #[cfg(test)]
    mod handle_ownership_tests {
        use super::*;
        use windows_sys::Win32::Foundation::GetHandleInformation;
        use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetProcessHandleCount};

        /// Non-zero while `handle` is still an open handle in this process.
        fn is_open(handle: HANDLE) -> bool {
            let mut flags: u32 = 0;
            (unsafe { GetHandleInformation(handle, &mut flags) }) != 0
        }

        fn handle_count() -> u32 {
            let mut count: u32 = 0;
            assert_ne!(
                unsafe { GetProcessHandleCount(GetCurrentProcess(), &mut count) },
                0,
                "GetProcessHandleCount failed"
            );
            count
        }

        fn new_pipe() -> (HANDLE, HANDLE) {
            let mut read: HANDLE = INVALID_HANDLE_VALUE;
            let mut write: HANDLE = INVALID_HANDLE_VALUE;
            assert_ne!(
                unsafe { CreatePipe(&mut read, &mut write, null_mut(), 0) },
                0,
                "CreatePipe failed"
            );
            (read, write)
        }

        #[test]
        fn owned_handle_closes_on_drop() {
            let (read, write) = new_pipe();
            assert!(is_open(read));

            drop(OwnedHandle(read));

            assert!(!is_open(read), "drop should have closed the handle");
            unsafe { CloseHandle(write) };
        }

        #[test]
        fn owned_handle_release_keeps_the_handle_open() {
            // The success path hands these to std::fs::File, so release must *not* close.
            let (read, write) = new_pipe();

            let released = OwnedHandle(read).release();

            assert_eq!(released, read);
            assert!(is_open(read), "release must leave the handle open");
            unsafe { CloseHandle(read) };
            unsafe { CloseHandle(write) };
        }

        #[test]
        fn owned_handle_tolerates_sentinel_values() {
            // Guards are constructed from out-params that stay at their sentinel when a call
            // fails, so dropping one of those must not call CloseHandle on garbage.
            drop(OwnedHandle(INVALID_HANDLE_VALUE));
            drop(OwnedHandle(0));
        }

        #[test]
        fn attribute_list_initializes_and_deletes() {
            // Also exercises the Drop path: DeleteProcThreadAttributeList on a list that was
            // successfully initialized. The success path never called this before.
            let mut list = OwnedAttributeList::with_capacity(1).expect("attribute list");
            assert!(!list.as_ptr().is_null());
        }

        // The regression test for #120 item 4. A command that cannot be launched drives
        // spawn_with_passthrough to its CreateProcessW failure exit, which used to return without
        // closing the two pipe handles it still owned - two kernel handles per attempt, forever.
        //
        // Asserted on this process's handle count rather than on internals: that is the property
        // that actually matters, and it fails loudly against the old code (~2 handles x 200
        // attempts) while being insensitive to how the fix is written.
        #[test]
        fn failed_spawn_does_not_leak_handles() {
            let bogus = "novaterminal-no-such-executable-4f2c9a.exe";

            // Warm up: the first few attempts touch lazily-initialized OS and CRT state, which
            // moves the handle count for reasons unrelated to the leak.
            for _ in 0..5 {
                let _ = spawn_with_passthrough(bogus, None, None, 80, 24, &[]);
            }

            let before = handle_count();
            const ATTEMPTS: u32 = 200;
            for _ in 0..ATTEMPTS {
                let result = spawn_with_passthrough(bogus, None, None, 80, 24, &[]);
                assert!(result.is_err(), "spawning {bogus} should fail");
            }
            let after = handle_count();

            // Generous ceiling: unrelated machinery may hold a few handles. The pre-fix leak is
            // 2 x ATTEMPTS = 400, so this is unambiguous either way.
            let growth = after.saturating_sub(before);
            assert!(
                growth < 20,
                "handle count grew by {growth} over {ATTEMPTS} failed spawns \
                 (before={before}, after={after}) - handles are leaking"
            );
        }
    }
}

use std::sync::{Arc, Mutex};

// Last-failure channel (#120 item 3).
//
// Every failure exit in pty_spawn_impl returned a bare null pointer, so the managed side could
// only ever say "Failed to create Rust PTY session." — "shell binary not found", "cwd does not
// exist" and "openpty failed" were indistinguishable, which is the single most common thing a
// user actually needs to know when a tab won't open.
//
// Thread-local rather than global, for the same reason errno is: two tabs can be spawning
// concurrently, and a global would let one overwrite the other's message. The managed caller
// reads it on the same thread that made the failing call, which is how RustPtySession works.
std::thread_local! {
    static LAST_ERROR: std::cell::RefCell<Option<String>> = const { std::cell::RefCell::new(None) };
}

fn set_last_error(message: impl Into<String>) {
    LAST_ERROR.with(|slot| *slot.borrow_mut() = Some(message.into()));
}

fn clear_last_error() {
    LAST_ERROR.with(|slot| *slot.borrow_mut() = None);
}

/// Copies the calling thread's most recent failure message into `buffer` as NUL-terminated UTF-8
/// and returns the number of bytes written, excluding the NUL. Returns 0 when there is nothing to
/// report, and -1 on invalid arguments.
///
/// The message is left in place, so it can be read more than once; the next `pty_spawn*` call on
/// this thread clears it.
///
/// # Safety
///
/// `buffer` must be null or point to at least `len` writable bytes. Null and non-positive `len`
/// are rejected rather than dereferenced.
// Suppressed rather than inherited: every other export in this file trips the same lint (it is
// inherent to a C ABI that takes pointers), but that is a pre-existing baseline of 8 and this
// annotation keeps a new export from growing it. Cleaning up the other 8 is its own change.
#[allow(clippy::not_unsafe_ptr_arg_deref)]
#[unsafe(no_mangle)]
pub extern "C" fn pty_last_error(buffer: *mut c_char, len: c_int) -> c_int {
    ffi_guard(-1, || {
        if buffer.is_null() || len <= 1 {
            // len == 1 leaves room for the NUL only, which cannot convey anything.
            return -1;
        }

        LAST_ERROR.with(|slot| {
            let borrowed = slot.borrow();
            let Some(message) = borrowed.as_deref() else {
                unsafe { *buffer = 0 };
                return 0;
            };

            let capacity = (len - 1) as usize;
            let bytes = message.as_bytes();
            // Truncate on a char boundary: a half-written multi-byte sequence would decode to
            // U+FFFD on the managed side, and the path/command names in these messages are
            // exactly the sort of thing that contains non-ASCII.
            let mut take = bytes.len().min(capacity);
            while take > 0 && !message.is_char_boundary(take) {
                take -= 1;
            }

            unsafe {
                std::ptr::copy_nonoverlapping(bytes.as_ptr(), buffer as *mut u8, take);
                *buffer.add(take) = 0;
            }
            take as c_int
        })
    })
}

/// Runs an FFI body, converting any panic into `on_panic` instead of unwinding
/// across the C boundary (undefined behavior). Asserted unwind-safe: FFI bodies
/// operate on raw pointers owned by the caller.
fn ffi_guard<R>(on_panic: R, body: impl FnOnce() -> R) -> R {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(value) => value,
        Err(_) => on_panic,
    }
}

// Structure to hold the PTY session state
pub struct PtyState {
    pub reader: Mutex<Box<dyn Read + Send>>,
    pub writer: Mutex<Box<dyn Write + Send>>,
    #[cfg(windows)]
    pub h_pc: Mutex<Option<windows_sys::Win32::System::Console::HPCON>>,
    #[cfg(windows)]
    pub h_process: Mutex<Option<windows_sys::Win32::Foundation::HANDLE>>,
    pub master: Mutex<Option<Box<dyn portable_pty::MasterPty + Send>>>,
    pub child: Mutex<Option<Box<dyn portable_pty::Child + Send>>>,
    // OS thread id of the thread currently blocked in pty_read's native ReadFile
    // (Windows portable path), or 0. pty_cancel_read uses it with
    // CancelSynchronousIo to unblock the read — killing the child / dropping the
    // master does NOT close the cloned reader handle on this path.
    #[cfg(windows)]
    pub read_thread_id: AtomicU32,
}

// Tokenize an argument string the way a POSIX-ish shell would: split on
// whitespace, but keep a double-quoted region as a single token and drop
// the surrounding quotes. Backslash escapes inside quotes are passed
// through (Windows paths use backslashes that should remain literal).
// Good enough for CommandBuilder's argv-style consumption -- we are not
// running a real shell here, just rebuilding argv from the test caller's
// pre-formatted argument string.
fn split_args(input: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut current = String::new();
    let mut in_quotes = false;
    let mut started = false;
    for ch in input.chars() {
        if in_quotes {
            if ch == '"' {
                in_quotes = false;
            } else {
                current.push(ch);
            }
        } else if ch == '"' {
            in_quotes = true;
            started = true;
        } else if ch.is_whitespace() {
            if started {
                out.push(std::mem::take(&mut current));
                started = false;
            }
        } else {
            current.push(ch);
            started = true;
        }
    }
    if started {
        out.push(current);
    }
    out
}

fn parse_env_overrides(envs: *const c_char) -> Vec<(String, String)> {
    if envs.is_null() {
        return Vec::new();
    }
    let raw = unsafe { CStr::from_ptr(envs).to_string_lossy() };
    let mut out = Vec::new();
    // Wire format: newline-separated KEY=VALUE pairs. Lines without '=' are
    // skipped. Values may contain '=' (only the first one splits).
    for line in raw.split('\n') {
        if line.is_empty() {
            continue;
        }
        if let Some((k, v)) = line.split_once('=') {
            out.push((k.to_string(), v.to_string()));
        }
    }
    out
}

/// Decide whether to bypass the Windows `PSEUDOCONSOLE_PASSTHROUGH` spawn path.
///
/// Passthrough silently drops a child's direct stdout writes (e.g. PowerShell 7's
/// VT prompt) when the host process has no real console -- which is always the case
/// for the GUI (WinExe) app, leaving pwsh tabs blank. Skip it then so we take the
/// portable-pty path whose pipe captures all child output. `env_opt_out` is the
/// explicit `NOVA_PTY_NO_PASSTHROUGH` override and always wins.
fn should_skip_passthrough(env_opt_out: bool, has_real_console: bool) -> bool {
    env_opt_out || !has_real_console
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_spawn(
    cmd: *const c_char,
    args: *const c_char,
    cwd: *const c_char,
    cols: u16,
    rows: u16,
) -> *mut PtyState {
    ffi_guard(std::ptr::null_mut(), || {
        pty_spawn_impl(cmd, args, cwd, cols, rows, &[], false)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_spawn_with_envs(
    cmd: *const c_char,
    args: *const c_char,
    cwd: *const c_char,
    cols: u16,
    rows: u16,
    envs: *const c_char,
) -> *mut PtyState {
    ffi_guard(std::ptr::null_mut(), || {
        let overrides = parse_env_overrides(envs);
        pty_spawn_impl(cmd, args, cwd, cols, rows, &overrides, false)
    })
}

/// `force_portable` is a test seam. Only the portable-pty path owns a `portable_pty::Child`, and
/// that is the path whose teardown #120 item 2 is about; the ConPTY passthrough path instead ends
/// its child as a side effect of ClosePseudoConsole. Whether a test run takes the passthrough path
/// depends on whether the runner has a real console, which is not something a test should depend
/// on - and steering it through NOVA_PTY_NO_PASSTHROUGH would mutate process-wide env while other
/// spawn tests run in parallel.
fn pty_spawn_impl(
    cmd: *const c_char,
    args: *const c_char,
    cwd: *const c_char,
    cols: u16,
    rows: u16,
    extra_envs: &[(String, String)],
    force_portable: bool,
) -> *mut PtyState {
    // Any message from a previous attempt on this thread is stale from here on.
    clear_last_error();

    let cmd_str = unsafe {
        if cmd.is_null() {
            set_last_error("cmd was null");
            return std::ptr::null_mut();
        }
        CStr::from_ptr(cmd).to_string_lossy()
    };
    let args_str = unsafe {
        if args.is_null() {
            None
        } else {
            Some(CStr::from_ptr(args).to_string_lossy())
        }
    };
    let cwd_str = unsafe {
        if cwd.is_null() {
            None
        } else {
            Some(CStr::from_ptr(cwd).to_string_lossy())
        }
    };

    #[cfg(windows)]
    {
        // PSEUDOCONSOLE_PASSTHROUGH silently swallows the child's stdout when the
        // host has no real console -- which is always true for the GUI (WinExe) app
        // and the xunit test runner, leaving e.g. pwsh 7 tabs blank. Take the
        // portable-pty path in that case. NOVA_PTY_NO_PASSTHROUGH stays as an
        // explicit override.
        let env_opt_out = std::env::var("NOVA_PTY_NO_PASSTHROUGH")
            .map(|v| v == "1" || v.eq_ignore_ascii_case("true"))
            .unwrap_or(false);
        let skip_passthrough = force_portable
            || should_skip_passthrough(env_opt_out, win32::host_has_real_console());
        if !skip_passthrough {
            let attempt = win32::spawn_with_passthrough(
                cmd_str.as_ref(),
                args_str.as_ref().map(|s| s.as_ref()),
                cwd_str.as_ref().map(|s| s.as_ref()),
                cols,
                rows,
                extra_envs,
            );
            // A passthrough failure is not fatal - we fall through to portable-pty below - so
            // record it as context rather than as the error. If the fallback also fails its
            // message replaces this one, which is the more useful of the two.
            if let Err(ref err) = attempt {
                set_last_error(format!("ConPTY passthrough spawn failed: {err}"));
            }
            if let Ok((reader, writer, h_pc, h_process)) = attempt {
                clear_last_error();
                let state = PtyState {
                    reader: Mutex::new(reader),
                    writer: Mutex::new(writer),
                    h_pc: Mutex::new(Some(h_pc)),
                    h_process: Mutex::new(Some(h_process)),
                    master: Mutex::new(None),
                    child: Mutex::new(None),
                    #[cfg(windows)]
                    read_thread_id: AtomicU32::new(0),
                };
                return Arc::into_raw(Arc::new(state)) as *mut PtyState;
            }
        }
    }

    // Fallback to portable-pty
    let system = NativePtySystem::default();
    let size = PtySize {
        rows,
        cols,
        pixel_width: 0,
        pixel_height: 0,
    };

    let pair = match system.openpty(size) {
        Ok(p) => p,
        Err(e) => {
            set_last_error(format!("openpty failed: {e}"));
            return std::ptr::null_mut();
        }
    };

    let mut cmd_builder = CommandBuilder::new(cmd_str.as_ref());
    if let Some(a) = args_str {
        // Plain split_whitespace would keep the surrounding " on a quoted
        // path like `--rcfile "C:\path with space\foo"`, which then breaks
        // the child (it tries to open a literal `"C:\path…` file). Parse
        // the argument string respecting double quotes so the child sees
        // the same argv it would from a shell.
        for arg in split_args(a.as_ref()) {
            if !arg.is_empty() {
                cmd_builder.arg(arg);
            }
        }
    }
    // Borrowed, not moved: the spawn failure message below names the cwd.
    if let Some(c) = cwd_str.as_ref() {
        if !c.is_empty() {
            cmd_builder.cwd(c.as_ref());
        }
    }
    cmd_builder.env("TERM", "xterm-256color");
    cmd_builder.env("COLORTERM", "truecolor");
    cmd_builder.env("TERM_PROGRAM", "NovaTerminal");
    // Inherit the user's locale. Forcing LC_ALL/LANG=C put every child shell in the
    // ASCII locale (mangled non-ASCII filenames, broken multibyte readline input, no
    // Unicode line drawing), contradicting the UTF-8 pipeline on the managed side.
    // Only if no locale is present at all, fall back to a UTF-8 charmap (#153):
    // glibc has C.UTF-8; Darwin doesn't, but its BSD locale system accepts the
    // bare "UTF-8" charmap for LC_CTYPE (bash would warn on LANG=C.UTF-8 there).
    if !cfg!(windows) {
        let has_locale = ["LC_ALL", "LC_CTYPE", "LANG"]
            .iter()
            .any(|k| std::env::var_os(k).is_some_and(|v| !v.is_empty()));
        if !has_locale {
            if cfg!(target_os = "macos") {
                cmd_builder.env("LC_CTYPE", "UTF-8");
            } else {
                cmd_builder.env("LANG", "C.UTF-8");
            }
        }
    }
    // Caller-supplied overrides last so shell-integration providers
    // (e.g. zsh's ZDOTDIR) can override the baseline.
    for (k, v) in extra_envs {
        cmd_builder.env(k.as_str(), v.as_str());
    }

    let child = match pair.slave.spawn_command(cmd_builder) {
        Ok(c) => c,
        Err(e) => {
            // The overwhelmingly common real failure: a shell path that does not exist, or a cwd
            // that does not. Naming the command and cwd is the whole point of this channel.
            set_last_error(format!(
                "failed to spawn '{}'{}: {e}",
                cmd_str,
                cwd_str
                    .as_deref()
                    .filter(|c| !c.is_empty())
                    .map(|c| format!(" in '{c}'"))
                    .unwrap_or_default()
            ));
            return std::ptr::null_mut();
        }
    };

    let reader = match pair.master.try_clone_reader() {
        Ok(r) => r,
        Err(e) => {
            set_last_error(format!("failed to clone PTY reader: {e}"));
            return std::ptr::null_mut();
        }
    };
    let writer = match pair.master.take_writer() {
        Ok(w) => w,
        Err(e) => {
            set_last_error(format!("failed to take PTY writer: {e}"));
            return std::ptr::null_mut();
        }
    };

    let state = PtyState {
        reader: Mutex::new(reader),
        writer: Mutex::new(writer),
        #[cfg(windows)]
        h_pc: Mutex::new(None),
        #[cfg(windows)]
        h_process: Mutex::new(None),
        master: Mutex::new(Some(pair.master)),
        child: Mutex::new(Some(child)),
        #[cfg(windows)]
        read_thread_id: AtomicU32::new(0),
    };

    Arc::into_raw(Arc::new(state)) as *mut PtyState
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_create(cmd: *const c_char, cols: u16, rows: u16) -> *mut PtyState {
    ffi_guard(std::ptr::null_mut(), || {
        pty_spawn(cmd, std::ptr::null(), std::ptr::null(), cols, rows)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_read(state_ptr: *mut PtyState, buffer: *mut u8, len: c_int) -> c_int {
    ffi_guard(-1, || {
        if state_ptr.is_null() || buffer.is_null() || len < 0 {
            return -1;
        }
        if len == 0 {
            return 0;
        }
        let state = unsafe {
            let arc = Arc::from_raw(state_ptr);
            let cloned = arc.clone();
            let _ = Arc::into_raw(arc);
            cloned
        };

        let buf = unsafe { std::slice::from_raw_parts_mut(buffer, len as usize) };
        if let Ok(mut reader) = state.reader.lock() {
            #[cfg(windows)]
            state.read_thread_id.store(
                unsafe { windows_sys::Win32::System::Threading::GetCurrentThreadId() },
                Ordering::SeqCst,
            );
            let result = match reader.read(buf) {
                Ok(n) => n as c_int,
                Err(e) => {
                    // pty_read still collapses every failure to -1, because the managed read loop
                    // treats the code as opaque and retries a bounded number of times either way.
                    // Recording *why* is what was missing: a permanently failing handle used to
                    // report nothing at all, so a frozen tab had no explanation anywhere (#107
                    // recorded this as blocked on #120's error channel).
                    set_last_error(format!("pty read failed: {e}"));
                    -1
                }
            };
            #[cfg(windows)]
            state.read_thread_id.store(0, Ordering::SeqCst);
            result
        } else {
            set_last_error("pty read failed: reader lock poisoned");
            -1
        }
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_write(state_ptr: *mut PtyState, buffer: *const u8, len: c_int) -> c_int {
    ffi_guard(-1, || {
        if state_ptr.is_null() || buffer.is_null() || len < 0 {
            return -1;
        }
        if len == 0 {
            return 0;
        }
        let state = unsafe {
            let arc = Arc::from_raw(state_ptr);
            let cloned = arc.clone();
            let _ = Arc::into_raw(arc);
            cloned
        };

        let buf = unsafe { std::slice::from_raw_parts(buffer, len as usize) };
        if let Ok(mut writer) = state.writer.lock() {
            // write_all, not write: a single write into a nearly-full PTY pipe can be
            // partial, and the C# caller has no way to retry the remainder — large
            // pastes silently lost bytes (#168).
            match writer.write_all(buf) {
                Ok(()) => len,
                Err(e) => {
                    set_last_error(format!("pty write failed: {e}"));
                    -1
                }
            }
        } else {
            set_last_error("pty write failed: writer lock poisoned");
            -1
        }
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_resize(state_ptr: *mut PtyState, cols: u16, rows: u16) {
    ffi_guard((), || {
        if state_ptr.is_null() {
            return;
        }
        let state = unsafe {
            let arc = Arc::from_raw(state_ptr);
            let cloned = arc.clone();
            let _ = Arc::into_raw(arc);
            cloned
        };

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

        if let Ok(master_opt) = state.master.lock() {
            if let Some(ref master) = *master_opt {
                let size = PtySize {
                    rows,
                    cols,
                    pixel_width: 0,
                    pixel_height: 0,
                };
                let _ = master.resize(size);
            }
        }
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_get_pid(state_ptr: *mut PtyState) -> c_int {
    ffi_guard(-1, || {
        if state_ptr.is_null() {
            return -1;
        }
        let state = unsafe {
            let arc = Arc::from_raw(state_ptr);
            let cloned = arc.clone();
            let _ = Arc::into_raw(arc);
            cloned
        };

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

        if let Ok(child_opt) = state.child.lock() {
            if let Some(ref child) = *child_opt {
                if let Some(pid) = child.process_id() {
                    return pid as c_int;
                }
            }
        }
        -1
    })
}

/// Unblock an in-flight `pty_read` so the caller's read thread can be joined.
///
/// The blocked read holds `state.reader`'s lock, so we must NEVER touch `reader`
/// here. We break the read from the other side, per platform/path:
///   * Windows passthrough: close the pseudoconsole (breaks the output pipe).
///   * Windows portable: the cloned reader handle is NOT closed by killing the
///     child or dropping the master, so cancel the blocking ReadFile directly via
///     CancelSynchronousIo against the recorded read thread.
///   * Unix: kill the child so the slave closes and the master read returns EOF.
/// Idempotent; safe to call before pty_close (it take()s the HPCON).
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

        #[cfg(windows)]
        {
            // Passthrough: closing the pseudoconsole breaks the output pipe so the
            // in-flight ReadFile on h_out_read returns. take() => pty_close won't
            // double-close.
            if let Ok(mut h_pc_opt) = state.h_pc.lock() {
                if let Some(h_pc) = h_pc_opt.take() {
                    unsafe {
                        windows_sys::Win32::System::Console::ClosePseudoConsole(h_pc);
                    }
                }
            }

            // Portable: cancel the blocking ReadFile on the recorded read thread.
            // Retry within a bounded window to cover the race where the read has
            // not yet entered ReadFile (CancelSynchronousIo => ERROR_NOT_FOUND).
            use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, ERROR_NOT_FOUND};
            use windows_sys::Win32::System::Threading::{OpenThread, THREAD_TERMINATE};
            use windows_sys::Win32::System::IO::CancelSynchronousIo;
            for _ in 0..100 {
                let tid = state.read_thread_id.load(Ordering::SeqCst);
                if tid == 0 {
                    // Not currently in a blocking read; brief wait then re-check.
                    std::thread::sleep(std::time::Duration::from_millis(10));
                    continue;
                }
                let h_thread = unsafe { OpenThread(THREAD_TERMINATE, 0, tid) };
                if h_thread == 0 {
                    break;
                }
                let cancelled = unsafe { CancelSynchronousIo(h_thread) };
                let last_err = unsafe { GetLastError() };
                unsafe { CloseHandle(h_thread) };
                if cancelled != 0 {
                    break; // an in-flight read was aborted
                }
                if last_err != ERROR_NOT_FOUND {
                    break; // unexpected; stop retrying
                }
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
        }

        // Kill the child. On Unix this closes the slave so the master read returns
        // EOF; on Windows it reaps the child after the read has been cancelled.
        if let Ok(mut child_opt) = state.child.lock() {
            if let Some(child) = child_opt.as_mut() {
                let _ = child.kill();
            }
        }
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_close(state_ptr: *mut PtyState) {
    ffi_guard((), || {
        if state_ptr.is_null() {
            return;
        }
        let state = unsafe { Arc::from_raw(state_ptr) };

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

        // Kill the child here too, not only in pty_cancel_read (#120 item 2).
        //
        // portable_pty::Child follows std::process::Child: dropping it does *not* kill the
        // process, it just stops observing it. So the comment that "drop logic handles the rest"
        // was true for the reader, writer and master and false for the child. The normal teardown
        // path is safe because RustPtySession.Dispose calls pty_cancel_read first, but its
        // exception-unwind branch deliberately skips the cancel - and any other caller that
        // reaches pty_close directly orphans the shell.
        //
        // Idempotent: kill() on an already-reaped child returns Err, which we ignore, and
        // pty_cancel_read having already killed it makes this a no-op.
        if let Ok(mut child_opt) = state.child.lock() {
            if let Some(child) = child_opt.as_mut() {
                let _ = child.kill();
            }
        }

        // Drop logic handles the rest (reader, writer, master)
    })
}

#[cfg(test)]
mod last_error_tests {
    use super::*;
    use std::ffi::CString;

    /// Calls pty_last_error into a buffer of `capacity` and returns (rc, decoded string).
    fn read_last_error(capacity: usize) -> (c_int, String) {
        let mut buf = vec![0i8; capacity];
        let rc = pty_last_error(buf.as_mut_ptr() as *mut c_char, capacity as c_int);
        let bytes: Vec<u8> = buf.iter().take_while(|b| **b != 0).map(|b| *b as u8).collect();
        (rc, String::from_utf8_lossy(&bytes).into_owned())
    }

    #[test]
    fn reports_nothing_when_there_is_no_error() {
        clear_last_error();
        let (rc, message) = read_last_error(64);
        assert_eq!(rc, 0);
        assert!(message.is_empty());
    }

    #[test]
    fn rejects_invalid_arguments() {
        assert_eq!(pty_last_error(std::ptr::null_mut(), 64), -1);

        let mut buf = [0i8; 4];
        // A one-byte buffer holds the NUL and nothing else, so it cannot convey a message.
        assert_eq!(pty_last_error(buf.as_mut_ptr() as *mut c_char, 1), -1);
        assert_eq!(pty_last_error(buf.as_mut_ptr() as *mut c_char, 0), -1);
    }

    #[test]
    fn returns_the_message_and_leaves_it_readable() {
        set_last_error("boom");

        let (rc, message) = read_last_error(64);
        assert_eq!(rc, 4);
        assert_eq!(message, "boom");

        // Readable more than once: the managed side logs and rethrows off the same value.
        let (rc2, again) = read_last_error(64);
        assert_eq!(rc2, 4);
        assert_eq!(again, "boom");
    }

    #[test]
    fn truncates_on_a_char_boundary() {
        // Three 4-byte emoji. A buffer with room for 6 payload bytes must stop after the first
        // one rather than splitting the second, which would decode to U+FFFD managed-side.
        set_last_error("\u{1F44D}\u{1F44D}\u{1F44D}");

        let (rc, message) = read_last_error(7);

        assert_eq!(rc, 4, "should have emitted exactly one whole emoji");
        assert_eq!(message, "\u{1F44D}");
    }

    #[test]
    fn truncation_can_emit_nothing_rather_than_a_partial_char() {
        set_last_error("\u{1F44D}");
        // Room for 2 payload bytes, but the only char needs 4.
        let (rc, message) = read_last_error(3);
        assert_eq!(rc, 0);
        assert!(message.is_empty());
    }

    // The point of the whole channel: a failed spawn must say *why*.
    #[test]
    fn failed_spawn_names_the_command() {
        let bogus = "novaterminal-no-such-shell-91b7fe";
        let c_cmd = CString::new(bogus).unwrap();
        let state = pty_spawn(
            c_cmd.as_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            80,
            24,
        );
        assert!(state.is_null(), "spawning {bogus} should fail");

        let (rc, message) = read_last_error(512);
        assert!(rc > 0, "expected a message, got rc={rc}");
        assert!(
            message.contains(bogus),
            "message should name the command; got: {message}"
        );
    }

    #[test]
    fn null_cmd_is_reported_rather_than_silently_null() {
        let state = pty_spawn(
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            80,
            24,
        );
        assert!(state.is_null());

        let (rc, message) = read_last_error(128);
        assert!(rc > 0);
        assert_eq!(message, "cmd was null");
    }

    #[test]
    fn a_successful_spawn_clears_a_previous_failure() {
        set_last_error("stale message from an earlier attempt");

        #[cfg(windows)]
        let (cmd, args) = ("cmd.exe", "/c exit");
        #[cfg(not(windows))]
        let (cmd, args) = ("/bin/sh", "-c 'exit 0'");

        let c_cmd = CString::new(cmd).unwrap();
        let c_args = CString::new(args).unwrap();
        let state = pty_spawn(
            c_cmd.as_ptr(),
            c_args.as_ptr(),
            std::ptr::null(),
            80,
            24,
        );
        assert!(!state.is_null(), "spawning {cmd} should succeed");

        let (rc, message) = read_last_error(128);
        assert_eq!(rc, 0, "stale message survived a successful spawn: {message}");

        pty_close(state);
    }
}

#[cfg(test)]
mod close_kills_child_tests {
    use super::*;
    use std::ffi::CString;

    // #120 item 2: portable_pty::Child follows std::process::Child - dropping it does not kill the
    // process. pty_close only closed handles and let the child drop, so any caller reaching
    // pty_close without first calling pty_cancel_read (notably RustPtySession.Dispose's
    // exception-unwind branch) orphaned the shell.
    //
    // Platform note, because it changes what this test proves. I mutation-checked it on Windows by
    // removing the new child.kill() from pty_close, and it still passed: on Windows dropping the
    // PtyState drops the ConPTY master, which closes the pseudoconsole, which ends the child as a
    // side effect. So on Windows the orphan does not reproduce and this test is only a guard.
    //
    // On Unix closing the master fd does *not* end a child that holds the slave, which is where the
    // orphan is real and where this test is load-bearing. It runs on Linux via the ubuntu leg of
    // the Rust FFI Tests job.
    #[test]
    fn close_without_cancel_terminates_the_child() {
        // A child that would outlive the test by a wide margin if it were not killed.
        #[cfg(windows)]
        let (cmd, args) = ("cmd.exe", "/c timeout /t 120 /nobreak >NUL");
        #[cfg(not(windows))]
        let (cmd, args) = ("/bin/sh", "-c 'sleep 120'");

        let c_cmd = CString::new(cmd).unwrap();
        let c_args = CString::new(args).unwrap();
        // force_portable: only the portable-pty path owns a `child`, and it is the one whose
        // teardown this test is about.
        let state = pty_spawn_impl(
            c_cmd.as_ptr(),
            c_args.as_ptr(),
            std::ptr::null(),
            80,
            24,
            &[],
            true,
        );
        assert!(!state.is_null(), "spawn failed");

        let pid = pty_get_pid(state);
        assert!(pid > 0, "expected a real pid, got {pid}");

        // Deliberately no pty_cancel_read: that is the path under test.
        pty_close(state);

        assert!(
            wait_for_process_exit(pid, std::time::Duration::from_secs(10)),
            "child pid {pid} survived pty_close - it has been orphaned"
        );
    }

    /// True once the process is gone, polling until `timeout`.
    fn wait_for_process_exit(pid: c_int, timeout: std::time::Duration) -> bool {
        let deadline = std::time::Instant::now() + timeout;
        while std::time::Instant::now() < deadline {
            if !process_is_alive(pid) {
                return true;
            }
            std::thread::sleep(std::time::Duration::from_millis(50));
        }
        !process_is_alive(pid)
    }

    #[cfg(windows)]
    fn process_is_alive(pid: c_int) -> bool {
        use windows_sys::Win32::Foundation::{CloseHandle, STILL_ACTIVE};
        use windows_sys::Win32::System::Threading::{
            GetExitCodeProcess, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
        };

        unsafe {
            let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid as u32);
            if handle == 0 {
                // Cannot open it at all: already reaped.
                return false;
            }
            let mut code: u32 = 0;
            let ok = GetExitCodeProcess(handle, &mut code);
            CloseHandle(handle);
            ok != 0 && code == STILL_ACTIVE as u32
        }
    }

    #[cfg(not(windows))]
    fn process_is_alive(pid: c_int) -> bool {
        // Signal 0 checks for existence without delivering anything. A zombie still counts as
        // alive here, but the child is reaped by portable-pty's own bookkeeping on kill.
        unsafe { libc::kill(pid, 0) == 0 }
    }
}

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

#[cfg(test)]
mod passthrough_decision_tests {
    use super::*;

    // PSEUDOCONSOLE_PASSTHROUGH drops a child's direct stdout writes (e.g. pwsh 7's
    // VT prompt) when the host process has no real console. A GUI (WinExe) app never
    // has one, so it must take the portable-pty path; otherwise pwsh tabs render blank.
    #[test]
    fn uses_passthrough_only_when_a_real_console_exists_and_not_opted_out() {
        assert!(!should_skip_passthrough(false, true), "console host, no opt-out -> keep passthrough");
    }

    #[test]
    fn skips_passthrough_when_no_real_console() {
        assert!(should_skip_passthrough(false, false), "GUI app (no console) -> must skip passthrough");
    }

    #[test]
    fn env_opt_out_always_skips() {
        assert!(should_skip_passthrough(true, true));
        assert!(should_skip_passthrough(true, false));
    }
}

#[cfg(test)]
mod cancel_read_tests {
    use super::*;
    use std::ffi::CString;
    use std::time::Instant;

    // After pty_cancel_read, a blocked pty_read must return promptly so the read
    // thread can be joined (guards #119 / the Dispose join). The reader loops and
    // drains any ConPTY startup chatter (e.g. an ESC[6n DSR query); once the child
    // is idle the read blocks, and only a working cancel ends the loop. Without a
    // working cancel the loop blocks forever and join() never completes.
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

        // Reader loop: keep reading (draining startup output) until a read returns
        // <= 0 (EOF, or aborted/errored by the cancel).
        let state_addr = state as usize;
        let reader = std::thread::spawn(move || {
            let ptr = state_addr as *mut PtyState;
            let mut buf = [0u8; 256];
            loop {
                let rc = pty_read(ptr, buf.as_mut_ptr(), buf.len() as c_int);
                if rc <= 0 {
                    return rc;
                }
            }
        });

        // Let startup output flush; the reader is now blocked on an idle child.
        std::thread::sleep(std::time::Duration::from_millis(1000));
        let start = Instant::now();
        pty_cancel_read(state);

        // The blocked read must return within a few seconds, not in ~30s.
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
