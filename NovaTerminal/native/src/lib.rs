use libc::{c_char, c_int};
use portable_pty::{CommandBuilder, NativePtySystem, PtySize, PtySystem};
use std::ffi::CStr;
use std::io::{Read, Write};

// Structure to hold the PTY session state
pub struct PtyState {
    pub reader: Box<dyn Read + Send>,
    pub writer: Box<dyn Write + Send>,
    pub master: Box<dyn portable_pty::MasterPty + Send>,
    pub child: Box<dyn portable_pty::Child + Send>,
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_create(cmd: *const c_char, cols: u16, rows: u16) -> *mut PtyState {
    // 1. Prepare the system
    let system = NativePtySystem::default();

    // 2. Configure size
    let size = PtySize {
        rows,
        cols,
        pixel_width: 0,
        pixel_height: 0,
    };

    // 3. Open PTY
    let pair = match system.openpty(size) {
        Ok(p) => p,
        Err(_) => return std::ptr::null_mut(),
    };

    // 4. Configure command
    let cmd_str = unsafe {
        assert!(!cmd.is_null());
        CStr::from_ptr(cmd).to_string_lossy()
    };

    // Split command string for arguments if necessary, usually it's just the shell
    // For simplicity, we assume 'cmd' is just the executable or we might need deeper parsing
    // But standard usage 'cmd.exe' or 'powershell.exe' works.
    let mut cmd_builder = CommandBuilder::new(cmd_str.as_ref());
    cmd_builder.env("TERM", "xterm-256color");

    // 5. Spawn
    let child = match pair.slave.spawn_command(cmd_builder) {
        Ok(c) => c,
        Err(_) => return std::ptr::null_mut(),
    };

    // 6. Get streams
    let reader = match pair.master.try_clone_reader() {
        Ok(r) => r,
        Err(_) => return std::ptr::null_mut(),
    };
    let writer = match pair.master.take_writer() {
        Ok(w) => w,
        Err(_) => return std::ptr::null_mut(),
    };

    // 7. Box and return
    let state = PtyState {
        reader,
        writer,
        master: pair.master,
        child,
    };

    Box::into_raw(Box::new(state))
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

    let size = PtySize {
        rows,
        cols,
        pixel_width: 0,
        pixel_height: 0,
    };

    // Debug log
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .append(true)
        .create(true)
        .open("rust_pty.log")
    {
        let _ = writeln!(f, "pty_resize: {}x{}", cols, rows);
    }

    let _ = state.master.resize(size);
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_get_pid(state_ptr: *mut PtyState) -> c_int {
    if state_ptr.is_null() {
        return -1;
    }
    let state = unsafe { &mut *state_ptr };

    // portable-pty child.id() might be u32 (pid), cast carefully
    if let Some(pid) = state.child.process_id() {
        pid as c_int
    } else {
        -1
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn pty_close(state_ptr: *mut PtyState) {
    if state_ptr.is_null() {
        return;
    }
    // Convert back to box to drop
    unsafe {
        let _ = Box::from_raw(state_ptr);
    }
}
