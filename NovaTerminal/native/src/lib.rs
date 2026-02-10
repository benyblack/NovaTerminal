use libc::{c_char, c_int};
use portable_pty::{CommandBuilder, NativePtySystem, PtySize, PtySystem};
use std::ffi::CStr;
use std::io::{Read, Write};

#[cfg(windows)]
mod win32 {
    use super::*;
    use std::os::windows::io::FromRawHandle;
    use std::ptr::{null, null_mut};
    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::System::Console::{
        ClosePseudoConsole, CreatePseudoConsole, COORD, HPCON,
    };
    use windows_sys::Win32::System::Pipes::CreatePipe;
    use windows_sys::Win32::System::Threading::{
        CreateProcessW, DeleteProcThreadAttributeList, InitializeProcThreadAttributeList,
        UpdateProcThreadAttribute, EXTENDED_STARTUPINFO_PRESENT, LPPROC_THREAD_ATTRIBUTE_LIST,
        PROCESS_INFORMATION, STARTUPINFOEXW,
    };

    pub const PSEUDOCONSOLE_PASSTHROUGH: u32 = 0x8;
    pub const PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE: usize = 0x00020016;

    pub fn spawn_with_passthrough(
        cmd: &str,
        args: Option<&str>,
        cwd: Option<&str>,
        cols: u16,
        rows: u16,
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
            let mut full_cmd = cmd.to_string();
            if let Some(a) = args {
                full_cmd.push(' ');
                full_cmd.push_str(a);
            }
            let mut cmd_utf16: Vec<u16> = full_cmd.encode_utf16().chain(Some(0)).collect();
            let cwd_utf16: Option<Vec<u16>> =
                cwd.map(|s| s.encode_utf16().chain(Some(0)).collect());

            // Prepare environment block with TERM=xterm-256color
            let mut env_map: std::collections::HashMap<String, String> = std::env::vars().collect();
            env_map.insert("TERM".to_string(), "xterm-256color".to_string());

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

// Structure to hold the PTY session state
pub struct PtyState {
    pub reader: Box<dyn Read + Send>,
    pub writer: Box<dyn Write + Send>,
    #[cfg(windows)]
    pub h_pc: Option<windows_sys::Win32::System::Console::HPCON>,
    #[cfg(windows)]
    pub h_process: Option<windows_sys::Win32::Foundation::HANDLE>,
    pub master: Option<Box<dyn portable_pty::MasterPty + Send>>,
    pub child: Option<Box<dyn portable_pty::Child + Send>>,
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_spawn(
    cmd: *const c_char,
    args: *const c_char,
    cwd: *const c_char,
    cols: u16,
    rows: u16,
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
        if let Ok((reader, writer, h_pc, h_process)) = win32::spawn_with_passthrough(
            cmd_str.as_ref(),
            args_str.as_ref().map(|s| s.as_ref()),
            cwd_str.as_ref().map(|s| s.as_ref()),
            cols,
            rows,
        ) {
            let state = PtyState {
                reader,
                writer,
                h_pc: Some(h_pc),
                h_process: Some(h_process),
                master: None,
                child: None,
            };
            return Box::into_raw(Box::new(state));
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
        for arg in a.split_whitespace() {
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
        reader,
        writer,
        #[cfg(windows)]
        h_pc: None,
        #[cfg(windows)]
        h_process: None,
        master: Some(pair.master),
        child: Some(child),
    };

    Box::into_raw(Box::new(state))
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_create(cmd: *const c_char, cols: u16, rows: u16) -> *mut PtyState {
    pty_spawn(cmd, std::ptr::null(), std::ptr::null(), cols, rows)
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_read(state_ptr: *mut PtyState, buffer: *mut u8, len: c_int) -> c_int {
    if state_ptr.is_null() {
        return -1;
    }
    let state = unsafe { &mut *state_ptr };

    let mut buf = vec![0u8; len as usize];
    match state.reader.read(&mut buf) {
        Ok(bytes_read) => {
            if bytes_read == 0 {
                return 0;
            } // EOF
            unsafe {
                std::ptr::copy_nonoverlapping(buf.as_ptr(), buffer, bytes_read);
            }
            bytes_read as c_int
        }
        Err(_) => -1,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_write(state_ptr: *mut PtyState, buffer: *const u8, len: c_int) -> c_int {
    if state_ptr.is_null() {
        return -1;
    }
    let state = unsafe { &mut *state_ptr };

    let buf = unsafe { std::slice::from_raw_parts(buffer, len as usize) };
    match state.writer.write(buf) {
        Ok(n) => {
            let _ = state.writer.flush();
            n as c_int
        }
        Err(_) => -1,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_resize(state_ptr: *mut PtyState, cols: u16, rows: u16) {
    if state_ptr.is_null() {
        return;
    }
    let state = unsafe { &mut *state_ptr };

    #[cfg(windows)]
    {
        if let Some(h_pc) = state.h_pc {
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

    if let Some(ref master) = state.master {
        let size = PtySize {
            rows,
            cols,
            pixel_width: 0,
            pixel_height: 0,
        };
        let _ = master.resize(size);
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_get_pid(state_ptr: *mut PtyState) -> c_int {
    if state_ptr.is_null() {
        return -1;
    }
    let state = unsafe { &mut *state_ptr };

    #[cfg(windows)]
    {
        if let Some(h_process) = state.h_process {
            unsafe {
                return windows_sys::Win32::System::Threading::GetProcessId(h_process) as c_int;
            }
        }
    }

    if let Some(ref child) = state.child {
        if let Some(pid) = child.process_id() {
            return pid as c_int;
        }
    }
    -1
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_close(state_ptr: *mut PtyState) {
    if state_ptr.is_null() {
        return;
    }
    let mut state = unsafe { Box::from_raw(state_ptr) };

    #[cfg(windows)]
    {
        if let Some(h_pc) = state.h_pc {
            unsafe {
                windows_sys::Win32::System::Console::ClosePseudoConsole(h_pc);
            }
        }
        if let Some(h_process) = state.h_process {
            unsafe {
                windows_sys::Win32::Foundation::CloseHandle(h_process);
            }
        }
    }
    // Drop logic handles the rest (reader, writer, master, child)
}
