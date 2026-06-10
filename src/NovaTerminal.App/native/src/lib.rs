use libc::{c_char, c_int};
use portable_pty::{CommandBuilder, NativePtySystem, PtySize, PtySystem};
use std::ffi::CStr;
use std::io::{Read, Write};
use std::panic::{catch_unwind, AssertUnwindSafe};

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
                return Err(anyhow::anyhow!("Failed to create input pipe"));
            }
            if CreatePipe(&mut h_out_read, &mut h_out_write, null_mut(), 0) == 0 {
                return Err(anyhow::anyhow!("Failed to create output pipe"));
            }

            let size = COORD {
                X: cols as i16,
                Y: rows as i16,
            };
            let mut h_pc: HPCON = 0;
            let res = CreatePseudoConsole(
                size,
                h_in_read,
                h_out_write,
                PSEUDOCONSOLE_PASSTHROUGH,
                &mut h_pc,
            );
            if res != 0 {
                return Err(anyhow::anyhow!("CreatePseudoConsole failed: {:x}", res));
            }

            // Close the handles we passed to the pseudoconsole
            CloseHandle(h_in_read);
            CloseHandle(h_out_write);

            let mut si_ex: STARTUPINFOEXW = std::mem::zeroed();
            si_ex.StartupInfo.cb = std::mem::size_of::<STARTUPINFOEXW>() as u32;

            let mut attr_size: usize = 0;
            InitializeProcThreadAttributeList(null_mut(), 1, 0, &mut attr_size);
            let mut attr_list = vec![0u8; attr_size];
            let lp_attr_list = attr_list.as_mut_ptr() as LPPROC_THREAD_ATTRIBUTE_LIST;
            InitializeProcThreadAttributeList(lp_attr_list, 1, 0, &mut attr_size);

            UpdateProcThreadAttribute(
                lp_attr_list,
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                h_pc as *const _,
                std::mem::size_of::<HPCON>(),
                null_mut(),
                null_mut(),
            );
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
                let err = std::io::Error::last_os_error();
                ClosePseudoConsole(h_pc);
                DeleteProcThreadAttributeList(lp_attr_list);
                return Err(anyhow::anyhow!("CreateProcessW failed: {}", err));
            }

            CloseHandle(pi.hThread);

            // Wrap handles in Rust types
            let reader = std::fs::File::from_raw_handle(h_out_read as _);
            let writer = std::fs::File::from_raw_handle(h_in_write as _);

            Ok((
                Box::new(reader) as Box<dyn Read + Send>,
                Box::new(writer) as Box<dyn Write + Send>,
                h_pc,
                pi.hProcess,
            ))
        }
    }
}

use std::sync::{Arc, Mutex};

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
        pty_spawn_impl(cmd, args, cwd, cols, rows, &[])
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
        pty_spawn_impl(cmd, args, cwd, cols, rows, &overrides)
    })
}

fn pty_spawn_impl(
    cmd: *const c_char,
    args: *const c_char,
    cwd: *const c_char,
    cols: u16,
    rows: u16,
    extra_envs: &[(String, String)],
) -> *mut PtyState {
    let cmd_str = unsafe {
        if cmd.is_null() {
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
        let skip_passthrough = should_skip_passthrough(env_opt_out, win32::host_has_real_console());
        if !skip_passthrough {
            if let Ok((reader, writer, h_pc, h_process)) = win32::spawn_with_passthrough(
                cmd_str.as_ref(),
                args_str.as_ref().map(|s| s.as_ref()),
                cwd_str.as_ref().map(|s| s.as_ref()),
                cols,
                rows,
                extra_envs,
            ) {
                let state = PtyState {
                    reader: Mutex::new(reader),
                    writer: Mutex::new(writer),
                    h_pc: Mutex::new(Some(h_pc)),
                    h_process: Mutex::new(Some(h_process)),
                    master: Mutex::new(None),
                    child: Mutex::new(None),
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
        Err(_) => return std::ptr::null_mut(),
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
    if let Some(c) = cwd_str {
        if !c.is_empty() {
            cmd_builder.cwd(c.as_ref());
        }
    }
    cmd_builder.env("TERM", "xterm-256color");
    cmd_builder.env("COLORTERM", "truecolor");
    cmd_builder.env("TERM_PROGRAM", "NovaTerminal");
    cmd_builder.env("LC_ALL", "C");
    cmd_builder.env("LANG", "C");
    // Caller-supplied overrides last so shell-integration providers
    // (e.g. zsh's ZDOTDIR) can override the baseline.
    for (k, v) in extra_envs {
        cmd_builder.env(k.as_str(), v.as_str());
    }

    let child = match pair.slave.spawn_command(cmd_builder) {
        Ok(c) => c,
        Err(_) => return std::ptr::null_mut(),
    };

    let reader = match pair.master.try_clone_reader() {
        Ok(r) => r,
        Err(_) => return std::ptr::null_mut(),
    };
    let writer = match pair.master.take_writer() {
        Ok(w) => w,
        Err(_) => return std::ptr::null_mut(),
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
            match reader.read(buf) {
                Ok(n) => n as c_int,
                Err(_) => -1,
            }
        } else {
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
            match writer.write(buf) {
                Ok(n) => n as c_int,
                Err(_) => -1,
            }
        } else {
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
        // Drop logic handles the rest (reader, writer, master, child)
    })
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
