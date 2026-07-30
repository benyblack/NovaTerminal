use libc::{c_char, c_int, c_void};
use russh::client::{self, AuthResult, KeyboardInteractiveAuthResponse};
use russh::keys::{PrivateKeyWithHashAlg, load_secret_key, ssh_key};
use russh::{ChannelMsg, Disconnect};
use russh_sftp::client::SftpSession;
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, VecDeque};
use std::ffi::{CStr, CString, OsStr, OsString};
use std::future::Future;
use std::io::Cursor;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Component, Path, PathBuf};
use std::ptr;
use std::sync::atomic::{AtomicI64, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, OnceLock, mpsc as std_mpsc};
use std::thread;
use std::time::Duration;
use tokio::fs::File as TokioFile;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::runtime::Builder;
use tokio::sync::mpsc;
use zeroize::Zeroizing;

const COPY_BUFFER_SIZE: usize = 64 * 1024;
const CANCELLATION_CHECK_INTERVAL_BYTES: u64 = 1024 * 1024;
const SHELL_DETECTION_TIMEOUT: Duration = Duration::from_secs(3);
const SHELL_DETECTION_MAX_OUTPUT_BYTES: usize = 4096;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct NovaSshEvent {
    pub kind: u32,
    pub payload_len: u32,
    pub status_code: i32,
    pub flags: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct NovaSshConnectArgs {
    pub host: *const c_char,
    pub user: *const c_char,
    pub port: u16,
    pub cols: u16,
    pub rows: u16,
    pub term: *const c_char,
    pub identity_file: *const c_char,
    pub jump_host: *const c_char,
    pub jump_user: *const c_char,
    pub jump_port: u16,
    pub keepalive_interval_seconds: u32,
    pub keepalive_count_max: u32,
    pub remote_shell_kind: u32,
    pub shell_detection_command: *const c_char,
    pub bash_cwd_bootstrap: *const c_char,
    pub zsh_cwd_bootstrap: *const c_char,
    pub fish_cwd_bootstrap: *const c_char,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct NovaSshDirectTcpIpArgs {
    pub host_to_connect: *const c_char,
    pub port_to_connect: u16,
    pub originator_address: *const c_char,
    pub originator_port: u16,
}

#[repr(u32)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NovaSshEventKind {
    None = 0,
    Connected = 1,
    Data = 2,
    HostKeyPrompt = 3,
    PasswordPrompt = 4,
    PassphrasePrompt = 5,
    KeyboardInteractivePrompt = 6,
    ExitStatus = 7,
    Error = 8,
    Closed = 9,
    ForwardChannelData = 10,
    ForwardChannelEof = 11,
    ForwardChannelClosed = 12,
}

#[repr(u32)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NovaSshResponseKind {
    HostKeyDecision = 1,
    Password = 2,
    Passphrase = 3,
    KeyboardInteractive = 4,
}

pub const NOVA_SSH_RESULT_OK: c_int = 0;
pub const NOVA_SSH_RESULT_EVENT_READY: c_int = 1;
pub const NOVA_SSH_RESULT_INVALID_ARGUMENT: c_int = -1;
pub const NOVA_SSH_RESULT_BUFFER_TOO_SMALL: c_int = -2;
pub const NOVA_SSH_RESULT_CLOSED: c_int = -3;
pub const NOVA_SSH_RESULT_CHANNEL_OPEN_FAILED: c_int = -4;
pub const NOVA_SSH_RESULT_NOT_IMPLEMENTED: c_int = -5;
pub const NOVA_SSH_RESULT_CANCELED: c_int = -6;
pub const NOVA_SSH_RESULT_PANIC: c_int = -7;

/// Runs an FFI body, converting any panic into `on_panic` instead of unwinding
/// across the C boundary (which is undefined behavior). The body is asserted
/// unwind-safe because FFI bodies operate on raw pointers owned by the caller.
fn ffi_guard<R>(on_panic: R, body: impl FnOnce() -> R) -> R {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(value) => value,
        Err(_) => on_panic,
    }
}

const NOVA_SSH_EVENT_FLAG_JSON: u32 = 1;
const NOVA_SSH_EVENT_FLAG_BINARY: u32 = 2;

pub struct NovaSshSession {
    shared: Arc<SharedState>,
    command_tx: Mutex<Option<mpsc::UnboundedSender<WorkerCommand>>>,
    worker: Mutex<Option<thread::JoinHandle<()>>>,
}

static SESSION_REGISTRY: OnceLock<Mutex<HashMap<u64, Arc<NovaSshSession>>>> = OnceLock::new();
static NEXT_SESSION_ID: AtomicU64 = AtomicU64::new(1);

#[cfg(debug_assertions)]
static OUTSTANDING_FFI_STRINGS: AtomicI64 = AtomicI64::new(0);

// Convert an owned String into a C string handed to the caller. In debug builds,
// tracks the outstanding count so tests can assert alloc/free balance.
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

fn session_registry() -> &'static Mutex<HashMap<u64, Arc<NovaSshSession>>> {
    SESSION_REGISTRY.get_or_init(|| Mutex::new(HashMap::new()))
}

fn lock_registry() -> std::sync::MutexGuard<'static, HashMap<u64, Arc<NovaSshSession>>> {
    session_registry().lock().unwrap_or_else(|p| p.into_inner())
}

/// Insert a session, returning a fresh non-zero handle id (0 is never issued).
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

/// Remove (close) a session, returning it if present. Second call ⇒ None (double-close).
fn registry_remove(handle: usize) -> Option<Arc<NovaSshSession>> {
    let id = handle as u64;
    if id == 0 {
        return None;
    }
    lock_registry().remove(&id)
}

/// Sends a command to the session's worker, returning OK or CLOSED.
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

struct SharedState {
    events: Mutex<VecDeque<QueuedEvent>>,
    responses: Mutex<VecDeque<QueuedResponse>>,
    response_cv: Condvar,
    closed: Mutex<bool>,
    // Async-side companion to `closed`/`response_cv`: lets the worker's session
    // establishment race against nova_ssh_close so a stuck connect/auth can be
    // aborted promptly instead of blocking `worker.join()` (and, transitively,
    // the .NET finalizer thread) indefinitely. See #155.
    closed_notify: tokio::sync::Notify,
}

struct QueuedEvent {
    kind: NovaSshEventKind,
    payload: Vec<u8>,
    status_code: i32,
    flags: u32,
}

/// A queued event's shape, without its payload. Lets the FFI report `payload_len` so the caller can
/// size a buffer, without copying anything (#173 item 1).
#[derive(Clone, Copy)]
struct EventMeta {
    kind: NovaSshEventKind,
    payload_len: usize,
    status_code: i32,
    flags: u32,
}

/// Outcome of a single `take_event_if_fits`.
enum EventRead {
    /// Nothing queued.
    Empty,
    /// Head event's payload exceeds the supplied capacity; it stays queued for a retry.
    TooSmall(EventMeta),
    /// Head event, removed from the queue and owned by the caller.
    Ready(QueuedEvent),
}

struct QueuedResponse {
    kind: NovaSshResponseKind,
    payload: Vec<u8>,
}

enum WorkerCommand {
    Write(Vec<u8>),
    Resize {
        cols: u16,
        rows: u16,
    },
    OpenDirectTcpIp {
        host_to_connect: String,
        port_to_connect: u32,
        originator_address: String,
        originator_port: u32,
        reply: std_mpsc::Sender<anyhow::Result<u32>>,
    },
    WriteForwardChannel {
        channel_id: u32,
        data: Vec<u8>,
    },
    ForwardChannelEof {
        channel_id: u32,
    },
    CloseForwardChannel {
        channel_id: u32,
    },
    Close,
}

#[derive(Clone)]
struct ConnectConfig {
    host: String,
    user: String,
    port: u16,
    cols: u16,
    rows: u16,
    term: String,
    identity_file: Option<String>,
    jump_host: Option<JumpHostConfig>,
    keepalive_interval_seconds: u32,
    keepalive_count_max: u32,
    remote_shell_kind: RemoteShellKind,
    shell_detection_command: Option<String>,
    bash_cwd_bootstrap: Option<String>,
    zsh_cwd_bootstrap: Option<String>,
    fish_cwd_bootstrap: Option<String>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum RemoteShellKind {
    Auto,
    Bash,
    Zsh,
    Fish,
    Pwsh,
}

#[derive(Clone)]
struct JumpHostConfig {
    host: String,
    user: String,
    port: u16,
}

#[derive(Clone)]
struct NovaClientHandler {
    shared: Arc<SharedState>,
    host: String,
    port: u16,
}

#[derive(Clone)]
struct TransferClientHandler {
    host: String,
    port: u16,
    known_hosts: NativeKnownHostsVerifier,
}

#[derive(Clone)]
struct TransferAuthConfig {
    /// Wrapped so the copy held for the lifetime of a transfer is wiped when the
    /// transfer ends, rather than lingering in the heap until the allocator reuses it.
    password: Option<Zeroizing<String>>,
    identity_file: Option<String>,
}

impl TransferAuthConfig {
    /// Moves the credential out of a deserialized request.
    ///
    /// `take` rather than `clone`: the password then exists as one allocation owned by
    /// this struct, wiped when it drops, instead of two independent copies with the
    /// request's copy outliving the transfer. Leaves `connection.password` as `None`,
    /// so the request cannot be a second source of the secret afterwards.
    fn take_from(connection: &mut SftpConnectionRequest) -> Self {
        Self {
            password: connection.password.take().map(Zeroizing::new),
            identity_file: connection.identity_file_path.clone(),
        }
    }
}

#[derive(Serialize)]
struct HostKeyPromptPayload<'a> {
    host: &'a str,
    port: u16,
    algorithm: String,
    fingerprint: String,
}

#[derive(Serialize)]
struct TextPromptPayload<'a> {
    prompt: &'a str,
}

#[derive(Serialize)]
struct KeyboardInteractivePromptPayload {
    name: String,
    instructions: String,
    prompts: Vec<KeyboardPromptPayload>,
}

#[derive(Serialize)]
struct KeyboardPromptPayload {
    prompt: String,
    echo: bool,
}

#[derive(Serialize)]
struct ConnectedPayload<'a> {
    host: &'a str,
    port: u16,
    user: &'a str,
}

#[derive(Serialize)]
struct ErrorPayload<'a> {
    message: &'a str,
}

#[derive(Serialize)]
struct ClosedPayload<'a> {
    reason: &'a str,
}

#[derive(Serialize)]
struct ExitStatusPayload {
    exit_status: u32,
}

#[derive(serde::Deserialize)]
struct HostKeyDecisionResponse {
    accept: bool,
}

#[derive(serde::Deserialize)]
struct TextResponse {
    text: String,
}

#[derive(serde::Deserialize)]
struct KeyboardInteractiveResponse {
    responses: Vec<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SftpTransferRequest {
    connection: SftpConnectionRequest,
    transfer: SftpTransferRequestBody,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SftpConnectionRequest {
    host: String,
    user: String,
    port: u16,
    password: Option<String>,
    identity_file_path: Option<String>,
    known_hosts_file_path: String,
    jump_host: Option<SftpJumpHostRequest>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SftpJumpHostRequest {
    host: String,
    user: Option<String>,
    port: u16,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SftpTransferRequestBody {
    direction: String,
    kind: String,
    local_path: String,
    remote_path: String,
    cancellation_marker_path: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RemotePathListRequest {
    connection: SftpConnectionRequest,
    path: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SftpTransferResponse<'a> {
    status: &'a str,
    message: &'a str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct RemotePathListResponse<'a> {
    status: &'a str,
    message: &'a str,
    entries: Vec<RemotePathListEntry>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct RemotePathListEntry {
    name: String,
    full_path: String,
    is_directory: bool,
    modified_at_unix_seconds: Option<u64>,
}

#[derive(Clone)]
struct NativeKnownHostsVerifier {
    entries: Arc<Vec<NativeKnownHostEntry>>,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct NativeKnownHostEntry {
    host: String,
    port: u16,
    algorithm: String,
    fingerprint: String,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct NovaSftpTransferProgressCallbackData {
    pub bytes_done: u64,
    pub bytes_total: u64,
    // ABI contract: current_path points to a transient UTF-8 string buffer that is
    // only valid for the duration of the callback invocation that receives it.
    pub current_path: *const c_char,
}

// ABI contract for native SFTP progress reporting:
// - callbacks are invoked synchronously during nova_ssh_sftp_transfer
// - progress_context is borrowed and only valid for that call duration
// - current_path in NovaSftpTransferProgressCallbackData is only valid for the
//   duration of the callback invocation
type NovaSftpTransferProgressCallback =
    unsafe extern "C" fn(*mut c_void, NovaSftpTransferProgressCallbackData);

#[derive(Clone, Copy)]
struct SftpProgressEmitter {
    callback: Option<NovaSftpTransferProgressCallback>,
    context: *mut c_void,
}

impl SftpProgressEmitter {
    fn emit(&self, bytes_done: u64, bytes_total: Option<u64>, current_path: &str) {
        let Some(callback) = self.callback else {
            return;
        };

        let current_path = match CString::new(current_path) {
            Ok(value) => value,
            Err(_) => return,
        };

        unsafe {
            callback(
                self.context,
                NovaSftpTransferProgressCallbackData {
                    bytes_done,
                    bytes_total: bytes_total.unwrap_or(0),
                    current_path: current_path.as_ptr(),
                },
            );
        }
    }
}

impl SharedState {
    fn new() -> Self {
        Self {
            events: Mutex::new(VecDeque::new()),
            responses: Mutex::new(VecDeque::new()),
            response_cv: Condvar::new(),
            closed: Mutex::new(false),
            closed_notify: tokio::sync::Notify::new(),
        }
    }

    fn is_closed(&self) -> bool {
        *self.closed.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Resolves once `mark_closed` has been called. Uses the create-notified-then-check
    /// pattern so a `mark_closed` racing between the check and the await is not missed.
    async fn wait_closed(&self) {
        loop {
            let notified = self.closed_notify.notified();
            if self.is_closed() {
                return;
            }
            notified.await;
        }
    }

    fn queue_event(&self, event: QueuedEvent) {
        if *self.closed.lock().unwrap_or_else(|e| e.into_inner()) {
            return;
        }

        self.events
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .push_back(event);
    }

    /// Removes and returns the head event if its payload fits in `payload_capacity`; otherwise
    /// reports its shape so the caller can size a buffer and retry, leaving it queued.
    ///
    /// One lock acquisition, and — the point — **no payload copy**. The previous shape was
    /// `peek_event()` (which cloned the whole payload) followed by `pop_event()`, so every event
    /// travelled through an extra `Vec` allocation and memcpy on the way out, and a
    /// `BUFFER_TOO_SMALL` retry threw that clone away and did it again. With the managed caller
    /// starting each poll at a zero-length buffer, that retry was not an edge case: it happened for
    /// *every* non-empty payload (#173 item 1).
    ///
    /// Doing it under a single lock also closes a latent TOCTOU in the old peek-then-pop pair: a
    /// second consumer could pop between the two calls, and the caller would then receive one
    /// event's metadata with another's payload. Only one consumer polls today, so this was
    /// unreachable rather than broken — worth removing while the code is open.
    fn take_event_if_fits(&self, payload_capacity: usize) -> EventRead {
        let mut events = self.events.lock().unwrap_or_else(|e| e.into_inner());

        let Some(front) = events.front() else {
            return EventRead::Empty;
        };

        let meta = EventMeta {
            kind: front.kind,
            payload_len: front.payload.len(),
            status_code: front.status_code,
            flags: front.flags,
        };

        if meta.payload_len > payload_capacity {
            return EventRead::TooSmall(meta);
        }

        // Moves the payload out; nothing is duplicated.
        match events.pop_front() {
            Some(event) => EventRead::Ready(event),
            None => EventRead::Empty,
        }
    }

    fn queue_response(&self, response: QueuedResponse) {
        self.responses
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .push_back(response);
        self.response_cv.notify_all();
    }

    fn wait_for_response(&self, kind: NovaSshResponseKind) -> Option<Vec<u8>> {
        let mut guard = self.responses.lock().unwrap_or_else(|e| e.into_inner());
        loop {
            if let Some(index) = guard.iter().position(|item| item.kind == kind) {
                return guard.remove(index).map(|item| item.payload);
            }

            if *self.closed.lock().unwrap_or_else(|e| e.into_inner()) {
                return None;
            }

            guard = self
                .response_cv
                .wait(guard)
                .unwrap_or_else(|e| e.into_inner());
        }
    }

    fn mark_closed(&self) {
        *self.closed.lock().unwrap_or_else(|e| e.into_inner()) = true;
        self.response_cv.notify_all();
        self.closed_notify.notify_waiters();
    }
}

impl client::Handler for NovaClientHandler {
    type Error = russh::Error;

    fn check_server_key(
        &mut self,
        server_public_key: &ssh_key::PublicKey,
    ) -> impl Future<Output = Result<bool, Self::Error>> + Send {
        let shared = self.shared.clone();
        let host = self.host.clone();
        let port = self.port;
        let algorithm = server_public_key.algorithm().to_string();
        let fingerprint = server_public_key
            .fingerprint(ssh_key::HashAlg::Sha256)
            .to_string();

        async move {
            if let Ok(payload) = serde_json::to_vec(&HostKeyPromptPayload {
                host: &host,
                port,
                algorithm,
                fingerprint,
            }) {
                shared.queue_event(QueuedEvent {
                    kind: NovaSshEventKind::HostKeyPrompt,
                    payload,
                    status_code: 0,
                    flags: NOVA_SSH_EVENT_FLAG_JSON,
                });
            }

            let response = match shared.wait_for_response(NovaSshResponseKind::HostKeyDecision) {
                Some(payload) => payload,
                None => return Ok(false),
            };

            let accept = serde_json::from_slice::<HostKeyDecisionResponse>(&response)
                .map(|value| value.accept)
                .unwrap_or(false);
            Ok(accept)
        }
    }
}

impl client::Handler for TransferClientHandler {
    type Error = anyhow::Error;

    fn check_server_key(
        &mut self,
        server_public_key: &ssh_key::PublicKey,
    ) -> impl Future<Output = Result<bool, Self::Error>> + Send {
        let known_hosts = self.known_hosts.clone();
        let host = self.host.clone();
        let port = self.port;
        let algorithm = server_public_key.algorithm().to_string();
        let fingerprint = server_public_key
            .fingerprint(ssh_key::HashAlg::Sha256)
            .to_string();

        async move {
            known_hosts.verify(&host, port, &algorithm, &fingerprint)?;
            Ok(true)
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_connect(args: *const NovaSshConnectArgs) -> usize {
    ffi_guard(0, || {
        let config = match ConnectConfig::from_args(args) {
            Some(config) => config,
            None => return 0,
        };

        let shared = Arc::new(SharedState::new());
        let (command_tx, command_rx) = mpsc::unbounded_channel();
        let worker_shared = shared.clone();
        let worker_config = config.clone();
        let worker = thread::spawn(move || {
            if let Err(error) = run_session(worker_config, worker_shared.clone(), command_rx) {
                worker_shared.queue_event(QueuedEvent {
                    kind: NovaSshEventKind::Error,
                    payload: serde_json::to_vec(&ErrorPayload {
                        message: &error.to_string(),
                    })
                    .unwrap_or_default(),
                    status_code: -1,
                    flags: NOVA_SSH_EVENT_FLAG_JSON,
                });
            }

            worker_shared.queue_event(QueuedEvent {
                kind: NovaSshEventKind::Closed,
                payload: serde_json::to_vec(&ClosedPayload {
                    reason: "session-ended",
                })
                .unwrap_or_default(),
                status_code: 0,
                flags: NOVA_SSH_EVENT_FLAG_JSON,
            });
            worker_shared.mark_closed();
        });

        let session = NovaSshSession {
            shared,
            command_tx: Mutex::new(Some(command_tx)),
            worker: Mutex::new(Some(worker)),
        };

        registry_insert(session) as usize
    })
}

#[unsafe(no_mangle)]
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
        // A null payload pointer can still carry a non-zero capacity from a caller that only wants
        // the header, so treat it as zero capacity: the event stays queued and the caller learns
        // payload_len from the header it just received.
        let effective_capacity = if payload.is_null() { 0 } else { payload_capacity };

        // Both outcomes report the same header, so resolve the metadata first and write it once.
        // Writing it per-arm would duplicate four raw-pointer stores for no benefit — and each store
        // is a separate `clippy::not_unsafe_ptr_arg_deref` site, so it would also have grown this
        // crate's lint baseline from 14 to 18 for a purely cosmetic reason.
        let (meta, delivered) = match session.shared.take_event_if_fits(effective_capacity) {
            EventRead::Empty => return NOVA_SSH_RESULT_OK,
            EventRead::TooSmall(meta) => (meta, None),
            EventRead::Ready(queued) => (
                EventMeta {
                    kind: queued.kind,
                    payload_len: queued.payload.len(),
                    status_code: queued.status_code,
                    flags: queued.flags,
                },
                Some(queued),
            ),
        };

        unsafe {
            (*event).kind = meta.kind as u32;
            (*event).payload_len = meta.payload_len as u32;
            (*event).status_code = meta.status_code;
            (*event).flags = meta.flags;
        }

        let Some(queued) = delivered else {
            // Still queued; the caller now knows how big a buffer to bring.
            return NOVA_SSH_RESULT_BUFFER_TOO_SMALL;
        };

        if !payload.is_null() && !queued.payload.is_empty() {
            unsafe {
                ptr::copy_nonoverlapping(queued.payload.as_ptr(), payload, queued.payload.len());
            }
        }

        NOVA_SSH_RESULT_EVENT_READY
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_write(
    handle: usize,
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

        send_command(&session, WorkerCommand::Write(bytes))
    })
}

#[unsafe(no_mangle)]
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

#[unsafe(no_mangle)]
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
            port_to_connect: if args.port_to_connect == 0 {
                0
            } else {
                args.port_to_connect as u32
            },
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

#[unsafe(no_mangle)]
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

        send_command(
            &session,
            WorkerCommand::WriteForwardChannel {
                channel_id,
                data: bytes,
            },
        )
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_channel_eof(handle: usize, channel_id: u32) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        let session = match registry_get(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };

        send_command(&session, WorkerCommand::ForwardChannelEof { channel_id })
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_channel_close(handle: usize, channel_id: u32) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        let session = match registry_get(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };

        send_command(&session, WorkerCommand::CloseForwardChannel { channel_id })
    })
}

#[unsafe(no_mangle)]
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

        // Auth and host-key prompts happen before the worker enters its shell loop,
        // so responses must bypass the command channel to avoid deadlocking startup.
        session
            .shared
            .queue_response(QueuedResponse { kind, payload });
        NOVA_SSH_RESULT_OK
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_close(handle: usize) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        let session = match registry_remove(handle) {
            Some(s) => s,
            None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
        };

        if let Some(tx) = session
            .command_tx
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .take()
        {
            let _ = tx.send(WorkerCommand::Close);
        }

        session.shared.mark_closed();

        if let Some(worker) = session
            .worker
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .take()
        {
            let _ = worker.join();
        }

        NOVA_SSH_RESULT_OK
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_sftp_transfer(
    request_json: *const c_char,
    progress_callback: Option<NovaSftpTransferProgressCallback>,
    progress_context: *mut c_void,
    response_json: *mut *mut c_char,
) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        if request_json.is_null() || response_json.is_null() {
            return NOVA_SSH_RESULT_INVALID_ARGUMENT;
        }

        unsafe {
            *response_json = ptr::null_mut();
        }

        let request_text = match unsafe { CStr::from_ptr(request_json) }.to_str() {
            Ok(value) => value,
            Err(_) => {
                return write_sftp_response_json(
                    response_json,
                    NOVA_SSH_RESULT_INVALID_ARGUMENT,
                    "invalid-argument",
                    "Native backend stub rejected a non-UTF8 SFTP request.",
                );
            }
        };

        let request = match serde_json::from_str::<SftpTransferRequest>(request_text) {
            Ok(value) => value,
            Err(_) => {
                return write_sftp_response_json(
                    response_json,
                    NOVA_SSH_RESULT_INVALID_ARGUMENT,
                    "invalid-argument",
                    "Native backend stub rejected invalid SFTP request JSON.",
                );
            }
        };

        if sftp_request_has_blank_fields(&request) {
            return write_sftp_response_json(
                response_json,
                NOVA_SSH_RESULT_INVALID_ARGUMENT,
                "invalid-argument",
                "Native backend stub rejected an incomplete SFTP request.",
            );
        }

        let progress = SftpProgressEmitter {
            callback: progress_callback,
            context: progress_context,
        };

        match run_sftp_transfer(request, progress) {
            Ok(()) => write_sftp_response_json(
                response_json,
                NOVA_SSH_RESULT_OK,
                "ok",
                "Native SFTP transfer completed.",
            ),
            Err(error) => {
                let (result, status, message) = classify_sftp_transfer_error(&error);
                write_sftp_response_json(response_json, result, status, &message)
            }
        }
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_sftp_list_directory(
    request_json: *const c_char,
    response_json: *mut *mut c_char,
) -> c_int {
    ffi_guard(NOVA_SSH_RESULT_PANIC, || {
        if request_json.is_null() || response_json.is_null() {
            return NOVA_SSH_RESULT_INVALID_ARGUMENT;
        }

        unsafe {
            *response_json = ptr::null_mut();
        }

        let request_text = match unsafe { CStr::from_ptr(request_json) }.to_str() {
            Ok(value) => value,
            Err(_) => {
                return write_sftp_response_json(
                    response_json,
                    NOVA_SSH_RESULT_INVALID_ARGUMENT,
                    "invalid-argument",
                    "Native backend stub rejected a non-UTF8 remote path list request.",
                );
            }
        };

        let request = match serde_json::from_str::<RemotePathListRequest>(request_text) {
            Ok(value) => value,
            Err(_) => {
                return write_sftp_response_json(
                    response_json,
                    NOVA_SSH_RESULT_INVALID_ARGUMENT,
                    "invalid-argument",
                    "Native backend stub rejected invalid remote path list request JSON.",
                );
            }
        };

        if remote_path_list_request_has_blank_fields(&request) {
            return write_sftp_response_json(
                response_json,
                NOVA_SSH_RESULT_INVALID_ARGUMENT,
                "invalid-argument",
                "Native backend stub rejected an incomplete remote path list request.",
            );
        }

        match run_remote_path_list(request) {
            Ok(entries) => write_remote_path_list_response_json(
                response_json,
                NOVA_SSH_RESULT_OK,
                "ok",
                "Native remote path listing completed.",
                entries,
            ),
            Err(error) => {
                let (result, status, message) = classify_sftp_transfer_error(&error);
                write_remote_path_list_response_json(
                    response_json,
                    result,
                    status,
                    &message,
                    Vec::new(),
                )
            }
        }
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_string_free(value: *mut c_char) {
    ffi_guard((), || {
        if !value.is_null() {
            #[cfg(debug_assertions)]
            OUTSTANDING_FFI_STRINGS.fetch_sub(1, Ordering::SeqCst);
            drop(unsafe { CString::from_raw(value) });
        }
    })
}

impl ConnectConfig {
    fn from_args(args: *const NovaSshConnectArgs) -> Option<Self> {
        let args = unsafe { args.as_ref()? };
        let host = read_c_string(args.host)?;
        let user = read_c_string(args.user)?;
        let term = read_c_string(args.term).unwrap_or_else(|| "xterm-256color".to_owned());
        let identity_file = read_c_string(args.identity_file);
        let jump_host = read_c_string(args.jump_host);
        let jump_user = read_c_string(args.jump_user);
        let effective_jump_host = jump_host.map(|host| JumpHostConfig {
            host,
            user: jump_user.unwrap_or_else(|| user.clone()),
            port: if args.jump_port == 0 {
                22
            } else {
                args.jump_port
            },
        });

        Some(Self {
            host,
            user,
            port: if args.port == 0 { 22 } else { args.port },
            cols: if args.cols == 0 { 120 } else { args.cols },
            rows: if args.rows == 0 { 30 } else { args.rows },
            term,
            identity_file,
            jump_host: effective_jump_host,
            keepalive_interval_seconds: if args.keepalive_interval_seconds == 0 {
                30
            } else {
                args.keepalive_interval_seconds
            },
            keepalive_count_max: if args.keepalive_count_max == 0 {
                3
            } else {
                args.keepalive_count_max
            },
            remote_shell_kind: parse_remote_shell_kind(args.remote_shell_kind),
            shell_detection_command: read_c_string(args.shell_detection_command),
            bash_cwd_bootstrap: read_c_string(args.bash_cwd_bootstrap),
            zsh_cwd_bootstrap: read_c_string(args.zsh_cwd_bootstrap),
            fish_cwd_bootstrap: read_c_string(args.fish_cwd_bootstrap),
        })
    }
}

fn parse_remote_shell_kind(value: u32) -> RemoteShellKind {
    match value {
        1 => RemoteShellKind::Bash,
        2 => RemoteShellKind::Zsh,
        3 => RemoteShellKind::Fish,
        4 => RemoteShellKind::Pwsh,
        _ => RemoteShellKind::Auto,
    }
}

fn detect_login_shell_output_to_kind(output: &str) -> RemoteShellKind {
    let trimmed = output.trim();
    if trimmed.is_empty() {
        return RemoteShellKind::Auto;
    }

    let token = trimmed
        .split_whitespace()
        .next()
        .unwrap_or(trimmed)
        .rsplit(['/', '\\'])
        .next()
        .unwrap_or(trimmed)
        .trim_end_matches(".exe")
        .to_ascii_lowercase();

    match token.as_str() {
        "bash" => RemoteShellKind::Bash,
        "zsh" => RemoteShellKind::Zsh,
        "fish" => RemoteShellKind::Fish,
        "pwsh" | "powershell" => RemoteShellKind::Pwsh,
        _ => RemoteShellKind::Auto,
    }
}

fn shell_single_quote(value: &str) -> String {
    value.replace('\'', "'\"'\"'")
}

fn wrap_posix_startup_command(command: String) -> String {
    format!("sh -lc '{}'", shell_single_quote(&command))
}

fn bash_login_startup() -> &'static str {
    "if [ -f ~/.bash_profile ]; then . ~/.bash_profile; \
elif [ -f ~/.bash_login ]; then . ~/.bash_login; \
elif [ -f ~/.profile ]; then . ~/.profile; \
fi"
}

fn zsh_login_startup() -> &'static str {
    "if [ -f ~/.zprofile ]; then source ~/.zprofile; fi"
}

fn append_bounded_shell_detection_output(output: &mut Vec<u8>, data: &[u8]) -> bool {
    let remaining = SHELL_DETECTION_MAX_OUTPUT_BYTES.saturating_sub(output.len());
    if remaining == 0 {
        return true;
    }

    output.extend_from_slice(&data[..data.len().min(remaining)]);
    output.len() >= SHELL_DETECTION_MAX_OUTPUT_BYTES
}

fn build_startup_command(shell_kind: RemoteShellKind, config: &ConnectConfig) -> Option<String> {
    match shell_kind {
        RemoteShellKind::Bash => {
            let bootstrap = config.bash_cwd_bootstrap.as_deref()?;
            Some(wrap_posix_startup_command(format!(
                "tmp_rc=$(mktemp)\ncat >\"$tmp_rc\" <<'__NOVA_BASHRC__'\n{bash_login_startup}\nif [ -f ~/.bashrc ]; then . ~/.bashrc; fi\n{bootstrap}\n__NOVA_BASHRC__\nexec bash --rcfile \"$tmp_rc\" -i",
                bash_login_startup = bash_login_startup()
            )))
        }
        RemoteShellKind::Zsh => {
            let bootstrap = config.zsh_cwd_bootstrap.as_deref()?;
            Some(wrap_posix_startup_command(format!(
                "tmp_dir=$(mktemp -d)\ncat >\"$tmp_dir/.zprofile\" <<'__NOVA_ZPROFILE__'\n{zsh_login_startup}\n__NOVA_ZPROFILE__\ncat >\"$tmp_dir/.zshrc\" <<'__NOVA_ZSHRC__'\nif [ -f ~/.zshrc ]; then source ~/.zshrc; fi\n{bootstrap}\n__NOVA_ZSHRC__\nZDOTDIR=\"$tmp_dir\" exec zsh -il",
                zsh_login_startup = zsh_login_startup()
            )))
        }
        RemoteShellKind::Fish => {
            let bootstrap = config.fish_cwd_bootstrap.as_deref()?;
            Some(wrap_posix_startup_command(format!(
                "tmp_dir=$(mktemp -d)\nmkdir -p \"$tmp_dir/fish\"\ncat >\"$tmp_dir/fish/config.fish\" <<'__NOVA_FISHRC__'\nif test -f ~/.config/fish/config.fish\n    source ~/.config/fish/config.fish\nend\n{bootstrap}\n__NOVA_FISHRC__\nXDG_CONFIG_HOME=\"$tmp_dir\" exec fish -i"
            )))
        }
        RemoteShellKind::Auto | RemoteShellKind::Pwsh => None,
    }
}

async fn detect_login_shell<H>(
    session: &mut client::Handle<H>,
    command: &str,
) -> anyhow::Result<RemoteShellKind>
where
    H: client::Handler + Send + 'static,
{
    let mut channel = session.channel_open_session().await?;
    channel.exec(true, command).await?;

    let detection_result = tokio::time::timeout(SHELL_DETECTION_TIMEOUT, async {
        let mut output = Vec::new();
        loop {
            match channel.wait().await {
                Some(ChannelMsg::Data { data }) => {
                    if append_bounded_shell_detection_output(&mut output, data.as_ref()) {
                        break;
                    }
                }
                Some(ChannelMsg::ExtendedData { .. }) => {}
                Some(ChannelMsg::ExitStatus { .. })
                | Some(ChannelMsg::ExitSignal { .. })
                | Some(ChannelMsg::Success)
                | Some(ChannelMsg::Failure)
                | Some(ChannelMsg::WindowAdjusted { .. })
                | Some(ChannelMsg::XonXoff { .. })
                | Some(ChannelMsg::Open { .. })
                | Some(ChannelMsg::OpenFailure(_)) => {}
                Some(ChannelMsg::Eof) | Some(ChannelMsg::Close) | None => break,
                Some(ChannelMsg::RequestPty { .. })
                | Some(ChannelMsg::RequestShell { .. })
                | Some(ChannelMsg::Exec { .. })
                | Some(ChannelMsg::Signal { .. })
                | Some(ChannelMsg::RequestSubsystem { .. })
                | Some(ChannelMsg::RequestX11 { .. })
                | Some(ChannelMsg::SetEnv { .. })
                | Some(ChannelMsg::WindowChange { .. })
                | Some(ChannelMsg::AgentForward { .. })
                | Some(_) => {}
            }
        }

        detect_login_shell_output_to_kind(String::from_utf8_lossy(&output).as_ref())
    })
    .await;

    let _ = channel.close().await;
    match detection_result {
        Ok(shell_kind) => Ok(shell_kind),
        Err(_) => Err(anyhow::anyhow!("shell detection timed out")),
    }
}

fn read_c_string(value: *const c_char) -> Option<String> {
    if value.is_null() {
        return None;
    }

    let string = unsafe { CStr::from_ptr(value) }
        .to_string_lossy()
        .trim()
        .to_owned();
    if string.is_empty() {
        None
    } else {
        Some(string)
    }
}

fn write_sftp_response_json(
    response_json: *mut *mut c_char,
    result: c_int,
    status: &str,
    message: &str,
) -> c_int {
    let response = SftpTransferResponse { status, message };
    let json = match serde_json::to_string(&response) {
        Ok(value) => value,
        Err(_) => return result,
    };

    let raw = ffi_string_into_raw(json);
    if raw.is_null() {
        return result;
    }
    unsafe {
        *response_json = raw;
    }
    result
}

fn write_remote_path_list_response_json(
    response_json: *mut *mut c_char,
    result: c_int,
    status: &str,
    message: &str,
    entries: Vec<RemotePathListEntry>,
) -> c_int {
    let response = RemotePathListResponse {
        status,
        message,
        entries,
    };
    let json = match serde_json::to_string(&response) {
        Ok(value) => value,
        Err(_) => return result,
    };

    let raw = ffi_string_into_raw(json);
    if raw.is_null() {
        return result;
    }
    unsafe {
        *response_json = raw;
    }
    result
}

fn sftp_request_has_blank_fields(request: &SftpTransferRequest) -> bool {
    let jump_host_is_blank = request
        .connection
        .jump_host
        .as_ref()
        .is_some_and(|jump_host| {
            jump_host.host.trim().is_empty()
                || jump_host
                    .user
                    .as_deref()
                    .is_some_and(|user| user.trim().is_empty())
                || jump_host.port == 0
        });

    request.connection.host.trim().is_empty()
        || request.connection.user.trim().is_empty()
        || request.connection.port == 0
        || request
            .connection
            .password
            .as_deref()
            .is_some_and(|password| password.trim().is_empty())
        || request
            .connection
            .identity_file_path
            .as_deref()
            .is_some_and(|path| path.trim().is_empty())
        || request.connection.known_hosts_file_path.trim().is_empty()
        || jump_host_is_blank
        || request.transfer.direction.trim().is_empty()
        || request.transfer.kind.trim().is_empty()
        || request.transfer.local_path.trim().is_empty()
        || request.transfer.remote_path.trim().is_empty()
        || request
            .transfer
            .cancellation_marker_path
            .as_deref()
            .is_some_and(|path| path.trim().is_empty())
}

fn remote_path_list_request_has_blank_fields(request: &RemotePathListRequest) -> bool {
    let jump_host_is_blank = request
        .connection
        .jump_host
        .as_ref()
        .is_some_and(|jump_host| {
            jump_host.host.trim().is_empty()
                || jump_host
                    .user
                    .as_deref()
                    .is_some_and(|user| user.trim().is_empty())
                || jump_host.port == 0
        });

    request.connection.host.trim().is_empty()
        || request.connection.user.trim().is_empty()
        || request.connection.port == 0
        || request
            .connection
            .password
            .as_deref()
            .is_some_and(|password| password.trim().is_empty())
        || request
            .connection
            .identity_file_path
            .as_deref()
            .is_some_and(|path| path.trim().is_empty())
        || request.connection.known_hosts_file_path.trim().is_empty()
        || jump_host_is_blank
        || request.path.trim().is_empty()
}

impl NativeKnownHostsVerifier {
    fn load(path: &str) -> anyhow::Result<Self> {
        let store_path = Path::new(path);
        if !store_path.exists() {
            return Ok(Self {
                entries: Arc::new(Vec::new()),
            });
        }

        let json = std::fs::read_to_string(store_path)?;
        let entries = serde_json::from_str::<Vec<NativeKnownHostEntry>>(&json)?;
        Ok(Self {
            entries: Arc::new(entries),
        })
    }

    fn verify(
        &self,
        host: &str,
        port: u16,
        algorithm: &str,
        fingerprint: &str,
    ) -> anyhow::Result<()> {
        let expected_host = host.trim();
        let expected_port = normalize_known_host_port(port);
        let expected_algorithm = normalize_known_host_algorithm(algorithm);
        let expected_fingerprint = normalize_known_host_fingerprint(fingerprint);

        let existing = self.entries.iter().find(|entry| {
            entry.host.trim().eq_ignore_ascii_case(expected_host)
                && normalize_known_host_port(entry.port) == expected_port
        });

        match existing {
            None => anyhow::bail!(
                "Unknown host key for {}:{}. Add the server key to the native known-hosts store before transferring files.",
                host,
                port
            ),
            Some(entry)
                if normalize_known_host_algorithm(&entry.algorithm) == expected_algorithm
                    && normalize_known_host_fingerprint(&entry.fingerprint)
                        == expected_fingerprint =>
            {
                Ok(())
            }
            Some(_) => anyhow::bail!(
                "Host key mismatch for {}:{}. The native known-hosts store entry does not match the server key.",
                host,
                port
            ),
        }
    }
}

fn normalize_known_host_port(port: u16) -> u16 {
    if port == 0 { 22 } else { port }
}

fn normalize_known_host_algorithm(algorithm: &str) -> String {
    algorithm.trim().to_owned()
}

fn normalize_known_host_fingerprint(fingerprint: &str) -> String {
    let normalized = fingerprint.trim();
    if normalized.is_empty() {
        return String::new();
    }

    const PREFIX: &str = "SHA256:";
    if normalized.len() >= PREFIX.len() && normalized[..PREFIX.len()].eq_ignore_ascii_case(PREFIX) {
        format!("{}{}", PREFIX, normalized[PREFIX.len()..].trim())
    } else {
        normalized.to_owned()
    }
}

fn run_sftp_transfer(
    request: SftpTransferRequest,
    progress: SftpProgressEmitter,
) -> anyhow::Result<()> {
    validate_supported_sftp_mode(&request.transfer)?;

    let runtime = Builder::new_current_thread().enable_all().build()?;
    runtime.block_on(async move {
        let mut request = request;
        let known_hosts =
            NativeKnownHostsVerifier::load(&request.connection.known_hosts_file_path)?;
        let client_config = Arc::new(client::Config::default());
        let auth = TransferAuthConfig::take_from(&mut request.connection);

        let jump_session = if let Some(jump_host) = &request.connection.jump_host {
            let jump_handler = TransferClientHandler {
                host: jump_host.host.clone(),
                port: jump_host.port,
                known_hosts: known_hosts.clone(),
            };

            let mut jump = client::connect(
                client_config.clone(),
                (jump_host.host.as_str(), jump_host.port),
                jump_handler,
            )
            .await?;

            let jump_user = jump_host
                .user
                .as_deref()
                .unwrap_or(request.connection.user.as_str());
            authenticate_transfer(jump_user, &auth, &mut jump).await?;
            Some(jump)
        } else {
            None
        };

        let target_handler = TransferClientHandler {
            host: request.connection.host.clone(),
            port: request.connection.port,
            known_hosts: known_hosts.clone(),
        };

        let mut session = if let Some(jump) = &jump_session {
            let stream = jump
                .channel_open_direct_tcpip(
                    request.connection.host.clone(),
                    request.connection.port as u32,
                    "127.0.0.1",
                    0,
                )
                .await?
                .into_stream();

            client::connect_stream(client_config.clone(), stream, target_handler).await?
        } else {
            client::connect(
                client_config.clone(),
                (request.connection.host.as_str(), request.connection.port),
                target_handler,
            )
            .await?
        };

        authenticate_transfer(&request.connection.user, &auth, &mut session).await?;
        perform_sftp_transfer(&mut session, &request.transfer, progress).await
    })
}

fn run_remote_path_list(
    request: RemotePathListRequest,
) -> anyhow::Result<Vec<RemotePathListEntry>> {
    let runtime = Builder::new_current_thread().enable_all().build()?;
    runtime.block_on(async move {
        let mut request = request;
        let known_hosts =
            NativeKnownHostsVerifier::load(&request.connection.known_hosts_file_path)?;
        let client_config = Arc::new(client::Config::default());
        let auth = TransferAuthConfig::take_from(&mut request.connection);

        let jump_session = if let Some(jump_host) = &request.connection.jump_host {
            let jump_handler = TransferClientHandler {
                host: jump_host.host.clone(),
                port: jump_host.port,
                known_hosts: known_hosts.clone(),
            };

            let mut jump = client::connect(
                client_config.clone(),
                (jump_host.host.as_str(), jump_host.port),
                jump_handler,
            )
            .await?;

            let jump_user = jump_host
                .user
                .as_deref()
                .unwrap_or(request.connection.user.as_str());
            authenticate_transfer(jump_user, &auth, &mut jump).await?;
            Some(jump)
        } else {
            None
        };

        let target_handler = TransferClientHandler {
            host: request.connection.host.clone(),
            port: request.connection.port,
            known_hosts: known_hosts.clone(),
        };

        let mut session = if let Some(jump) = &jump_session {
            let stream = jump
                .channel_open_direct_tcpip(
                    request.connection.host.clone(),
                    request.connection.port as u32,
                    "127.0.0.1",
                    0,
                )
                .await?
                .into_stream();

            client::connect_stream(client_config.clone(), stream, target_handler).await?
        } else {
            client::connect(
                client_config.clone(),
                (request.connection.host.as_str(), request.connection.port),
                target_handler,
            )
            .await?
        };

        authenticate_transfer(&request.connection.user, &auth, &mut session).await?;
        list_remote_directory(&mut session, &request.path).await
    })
}

async fn authenticate_transfer<H>(
    user: &str,
    auth: &TransferAuthConfig,
    session: &mut client::Handle<H>,
) -> anyhow::Result<()>
where
    H: client::Handler + Send + 'static,
{
    if let Some(identity_file) = auth.identity_file.as_deref() {
        let key = load_secret_key(Path::new(identity_file), None).map_err(|_| {
            anyhow::anyhow!(
                "Failed to load identity file '{}' for non-interactive native SFTP auth. Encrypted keys require interactive passphrase entry, which is not available for transfers.",
                identity_file
            )
        })?;

        let hash_alg = session.best_supported_rsa_hash().await?.flatten();
        let result = session
            .authenticate_publickey(
                user.to_owned(),
                PrivateKeyWithHashAlg::new(Arc::new(key), hash_alg),
            )
            .await?;
        if result.success() {
            return Ok(());
        }

        anyhow::bail!("Authentication failed.");
    }

    if let Some(password) = auth.password.as_deref() {
        // russh's API takes an owned String, so this copy is unavoidable and its
        // lifetime is russh's to manage. Our own copy is still wiped when `auth` drops.
        let result = session
            .authenticate_password(user.to_owned(), password.to_owned())
            .await?;
        if result.success() {
            return Ok(());
        }

        anyhow::bail!("Authentication failed.");
    }

    anyhow::bail!(
        "Native SFTP transfer requires either a password or an identity file for non-interactive authentication."
    )
}

async fn perform_sftp_transfer<H>(
    session: &mut client::Handle<H>,
    transfer: &SftpTransferRequestBody,
    progress: SftpProgressEmitter,
) -> anyhow::Result<()>
where
    H: client::Handler + Send + 'static,
{
    validate_supported_sftp_mode(transfer)?;

    let channel = session.channel_open_session().await?;
    channel.request_subsystem(true, "sftp").await?;
    let sftp = SftpSession::new(channel.into_stream()).await?;
    let direction = transfer.direction.trim().to_ascii_lowercase();
    let kind = transfer.kind.trim().to_ascii_lowercase();
    let cancellation_marker_path = transfer.cancellation_marker_path.as_deref();
    let mut copy_buffer = vec![0u8; COPY_BUFFER_SIZE];
    ensure_transfer_not_canceled(cancellation_marker_path)?;

    match (direction.as_str(), kind.as_str()) {
        ("download", "file") => {
            download_file_from_remote(
                &sftp,
                &transfer.remote_path,
                Path::new(&transfer.local_path),
                cancellation_marker_path,
                progress,
                &mut copy_buffer,
            )
            .await?;
        }
        ("upload", "file") => {
            let remote_target = resolve_upload_file_target(
                &sftp,
                Path::new(&transfer.local_path),
                &transfer.remote_path,
            )
            .await?;
            upload_file_to_remote(
                &sftp,
                Path::new(&transfer.local_path),
                &remote_target,
                cancellation_marker_path,
                progress,
                &mut copy_buffer,
            )
            .await?;
        }
        ("download", "directory") => {
            let local_root =
                PathBuf::from(&transfer.local_path).join(remote_basename(&transfer.remote_path)?);
            download_directory_from_remote(
                &sftp,
                &transfer.remote_path,
                &local_root,
                cancellation_marker_path,
                progress,
                &mut copy_buffer,
            )
            .await?;
        }
        ("upload", "directory") => {
            let local_root = PathBuf::from(&transfer.local_path);
            let remote_root =
                resolve_upload_directory_target(&sftp, &local_root, &transfer.remote_path).await?;
            upload_directory_to_remote(
                &sftp,
                &local_root,
                &remote_root,
                cancellation_marker_path,
                progress,
                &mut copy_buffer,
            )
            .await?;
        }
        _ => anyhow::bail!(
            "Native SFTP transfer mode '{}/{}' is not implemented yet.",
            direction,
            kind
        ),
    }

    sftp.close().await?;
    Ok(())
}

async fn list_remote_directory<H>(
    session: &mut client::Handle<H>,
    remote_path: &str,
) -> anyhow::Result<Vec<RemotePathListEntry>>
where
    H: client::Handler + Send + 'static,
{
    let channel = session.channel_open_session().await?;
    channel.request_subsystem(true, "sftp").await?;
    let sftp = SftpSession::new(channel.into_stream()).await?;

    let home_directory = if remote_path.trim().starts_with('~') {
        Some(
            sftp.canonicalize(".")
                .await
                .map_err(|error| map_remote_transfer_error(".", error))?,
        )
    } else {
        None
    };
    let expanded_remote_path = expand_remote_home_path(remote_path, home_directory.as_deref())?;
    let mut entries = sftp
        .read_dir(expanded_remote_path.clone())
        .await
        .map_err(|error| map_remote_transfer_error(&expanded_remote_path, error))?
        .collect::<Vec<_>>();
    entries.sort_by_key(|entry| entry.file_name());

    let mapped = entries
        .into_iter()
        .map(|entry| {
            let name = entry.file_name();
            let full_path = join_remote_path(&expanded_remote_path, &name);
            RemotePathListEntry {
                name,
                full_path,
                is_directory: entry.metadata().is_dir(),
                modified_at_unix_seconds: entry.metadata().mtime.map(|value| value as u64),
            }
        })
        .collect();

    sftp.close().await?;
    Ok(mapped)
}

fn validate_supported_sftp_mode(transfer: &SftpTransferRequestBody) -> anyhow::Result<()> {
    let direction = transfer.direction.trim().to_ascii_lowercase();
    let kind = transfer.kind.trim().to_ascii_lowercase();
    if (direction == "download" || direction == "upload") && (kind == "file" || kind == "directory")
    {
        return Ok(());
    }

    Err(anyhow::Error::new(NativeSftpTransferError::new(
        NativeSftpTransferErrorKind::NotImplemented,
        format!(
            "Native SFTP transfer mode '{}/{}' is not implemented yet.",
            direction, kind
        ),
    )))
}

async fn download_file_from_remote(
    sftp: &SftpSession,
    remote_path: &str,
    local_path: &Path,
    cancellation_marker_path: Option<&str>,
    progress: SftpProgressEmitter,
    copy_buffer: &mut [u8],
) -> anyhow::Result<()> {
    let total_bytes = sftp
        .metadata(remote_path.to_owned())
        .await
        .map(|metadata| metadata.size)
        .unwrap_or(None);
    let mut remote_file = sftp
        .open(remote_path.to_owned())
        .await
        .map_err(|error| map_remote_transfer_error(remote_path, error))?;
    if let Some(parent) = local_path.parent() {
        if !parent.as_os_str().is_empty() {
            tokio::fs::create_dir_all(parent)
                .await
                .map_err(|error| map_local_transfer_error(parent, error))?;
        }
    }

    // Download into a sibling `.novapart` file and rename on success. Writing straight
    // to `local_path` would truncate an existing good copy the moment the file is
    // created — before a single byte arrives — so a cancelled or failed re-download
    // used to destroy the previous version and leave a truncated file behind.
    let partial_path = partial_download_path(local_path);
    let mut local_file = create_partial_download_file(&partial_path).await?;

    if let Err(error) = copy_file_with_cancellation(
        &mut remote_file,
        &mut local_file,
        cancellation_marker_path,
        total_bytes,
        remote_path,
        progress,
        copy_buffer,
    )
    .await
    {
        drop(local_file);
        discard_partial_download(&partial_path).await;
        return Err(error);
    }

    if let Err(error) = local_file.flush().await {
        drop(local_file);
        discard_partial_download(&partial_path).await;
        return Err(map_local_transfer_error(&partial_path, error));
    }

    // Close the handle before renaming: Windows refuses to rename a file that still
    // has an open handle.
    drop(local_file);

    // Close the remote handle *before* the rename so that the rename is the last
    // fallible operation, and therefore the single commit point. With the rename first,
    // a failing shutdown would report the transfer as failed even though the
    // destination had already been replaced - and in a directory download would abort
    // the remaining files having already committed this one.
    if let Err(error) = remote_file.shutdown().await {
        discard_partial_download(&partial_path).await;
        return Err(error.into());
    }

    if let Err(error) = tokio::fs::rename(&partial_path, local_path).await {
        discard_partial_download(&partial_path).await;
        return Err(map_local_transfer_error(local_path, error));
    }

    Ok(())
}

async fn upload_file_to_remote(
    sftp: &SftpSession,
    local_path: &Path,
    remote_path: &str,
    cancellation_marker_path: Option<&str>,
    progress: SftpProgressEmitter,
    copy_buffer: &mut [u8],
) -> anyhow::Result<()> {
    let total_bytes = std::fs::metadata(local_path)
        .ok()
        .map(|metadata| metadata.len());
    let mut local_file = TokioFile::open(local_path)
        .await
        .map_err(|error| map_local_transfer_error(local_path, error))?;
    let mut remote_file = sftp
        .create(remote_path.to_owned())
        .await
        .map_err(|error| map_remote_transfer_error(remote_path, error))?;
    copy_file_with_cancellation(
        &mut local_file,
        &mut remote_file,
        cancellation_marker_path,
        total_bytes,
        &local_path.to_string_lossy(),
        progress,
        copy_buffer,
    )
    .await?;
    remote_file.shutdown().await?;
    Ok(())
}

async fn resolve_upload_file_target(
    sftp: &SftpSession,
    local_path: &Path,
    remote_path: &str,
) -> anyhow::Result<String> {
    let local_name = local_path
        .file_name()
        .and_then(|value| value.to_str())
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| anyhow::anyhow!("Local file name is required for native SFTP upload."))?;
    let home_directory = if remote_path.trim().starts_with('~') {
        Some(
            sftp.canonicalize(".")
                .await
                .map_err(|error| map_remote_transfer_error(".", error))?,
        )
    } else {
        None
    };
    let expanded_remote_path = expand_remote_home_path(remote_path, home_directory.as_deref())?;
    let remote_path_is_dir = if sftp
        .try_exists(expanded_remote_path.clone())
        .await
        .map_err(|error| map_remote_transfer_error(&expanded_remote_path, error))?
    {
        sftp.metadata(expanded_remote_path.clone())
            .await
            .map_err(|error| map_remote_transfer_error(&expanded_remote_path, error))?
            .is_dir()
    } else {
        false
    };

    resolve_upload_file_destination_path(
        local_name,
        remote_path,
        home_directory.as_deref(),
        remote_path_is_dir,
    )
}

async fn download_directory_from_remote(
    sftp: &SftpSession,
    remote_root: &str,
    local_root: &Path,
    cancellation_marker_path: Option<&str>,
    progress: SftpProgressEmitter,
    copy_buffer: &mut [u8],
) -> anyhow::Result<()> {
    tokio::fs::create_dir_all(local_root)
        .await
        .map_err(|error| map_local_transfer_error(local_root, error))?;

    let mut pending = VecDeque::from([(remote_root.to_owned(), local_root.to_path_buf())]);
    while let Some((remote_dir, local_dir)) = pending.pop_front() {
        ensure_transfer_not_canceled(cancellation_marker_path)?;
        tokio::fs::create_dir_all(&local_dir)
            .await
            .map_err(|error| map_local_transfer_error(&local_dir, error))?;

        let mut entries = sftp
            .read_dir(remote_dir.clone())
            .await
            .map_err(|error| map_remote_transfer_error(&remote_dir, error))?
            .collect::<Vec<_>>();
        entries.sort_by_key(|entry| entry.file_name());

        for entry in entries {
            ensure_transfer_not_canceled(cancellation_marker_path)?;
            let file_name = entry.file_name();
            validate_remote_entry_name(&file_name)?;
            let remote_child = join_remote_path(&remote_dir, &file_name);
            let local_child = local_dir.join(&file_name);
            ensure_within_download_root(local_root, &local_child)?;
            let metadata = entry.metadata();

            if metadata.is_dir() {
                pending.push_back((remote_child, local_child));
                continue;
            }

            if metadata.is_regular() {
                download_file_from_remote(
                    sftp,
                    &remote_child,
                    &local_child,
                    cancellation_marker_path,
                    progress,
                    copy_buffer,
                )
                .await?;
            }
        }
    }

    Ok(())
}

async fn upload_directory_to_remote(
    sftp: &SftpSession,
    local_root: &Path,
    remote_root: &str,
    cancellation_marker_path: Option<&str>,
    progress: SftpProgressEmitter,
    copy_buffer: &mut [u8],
) -> anyhow::Result<()> {
    ensure_remote_directory_exists(sftp, remote_root).await?;

    let mut pending = VecDeque::from([(local_root.to_path_buf(), remote_root.to_owned())]);
    while let Some((local_dir, remote_dir)) = pending.pop_front() {
        ensure_transfer_not_canceled(cancellation_marker_path)?;
        let mut entries =
            std::fs::read_dir(&local_dir)?.collect::<Result<Vec<_>, std::io::Error>>()?;
        entries.sort_by_key(|entry| entry.file_name());

        for entry in entries {
            ensure_transfer_not_canceled(cancellation_marker_path)?;
            let local_child = entry.path();
            let file_name = entry.file_name();
            let file_name = file_name.to_string_lossy().into_owned();
            let remote_child = join_remote_path(&remote_dir, &file_name);
            let metadata = std::fs::symlink_metadata(&local_child)?;

            if metadata.is_dir() {
                ensure_remote_directory_exists(sftp, &remote_child).await?;
                pending.push_back((local_child, remote_child));
                continue;
            }

            if metadata.is_file() {
                upload_file_to_remote(
                    sftp,
                    &local_child,
                    &remote_child,
                    cancellation_marker_path,
                    progress,
                    copy_buffer,
                )
                .await?;
            }
        }
    }

    Ok(())
}

async fn resolve_upload_directory_target(
    sftp: &SftpSession,
    local_root: &Path,
    remote_path: &str,
) -> anyhow::Result<String> {
    let local_name = local_root
        .file_name()
        .and_then(|value| value.to_str())
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| {
            anyhow::anyhow!("Local directory name is required for native SFTP upload.")
        })?;
    let normalized_remote = normalize_remote_directory_path(remote_path)?;

    if sftp
        .try_exists(normalized_remote.clone())
        .await
        .map_err(|error| map_remote_transfer_error(&normalized_remote, error))?
    {
        let metadata = sftp
            .metadata(normalized_remote.clone())
            .await
            .map_err(|error| map_remote_transfer_error(&normalized_remote, error))?;
        if metadata.is_dir() {
            return Ok(join_remote_path(&normalized_remote, local_name));
        }
    }

    Ok(normalized_remote)
}

async fn ensure_remote_directory_exists(sftp: &SftpSession, path: &str) -> anyhow::Result<()> {
    let normalized = normalize_remote_directory_path(path)?;
    if normalized == "/" {
        return Ok(());
    }

    let is_absolute = normalized.starts_with('/');
    let mut current = if is_absolute {
        String::from("/")
    } else {
        String::new()
    };

    for part in normalized.split('/').filter(|part| !part.is_empty()) {
        current = if current == "/" {
            format!("/{}", part)
        } else if current.is_empty() {
            part.to_owned()
        } else {
            format!("{}/{}", current, part)
        };

        if !sftp
            .try_exists(current.clone())
            .await
            .map_err(|error| map_remote_transfer_error(&current, error))?
        {
            sftp.create_dir(current.clone())
                .await
                .map_err(|error| map_remote_transfer_error(&current, error))?;
        }
    }

    Ok(())
}

async fn copy_file_with_cancellation<R, W>(
    reader: &mut R,
    writer: &mut W,
    cancellation_marker_path: Option<&str>,
    total_bytes: Option<u64>,
    current_path: &str,
    progress: SftpProgressEmitter,
    copy_buffer: &mut [u8],
) -> anyhow::Result<()>
where
    R: AsyncReadExt + Unpin,
    W: AsyncWriteExt + Unpin,
{
    let mut bytes_done = 0u64;
    let mut bytes_since_cancellation_check = 0u64;
    ensure_transfer_not_canceled(cancellation_marker_path)?;
    loop {
        let read = reader.read(copy_buffer).await?;
        if read == 0 {
            break;
        }

        writer.write_all(&copy_buffer[..read]).await?;
        bytes_done += read as u64;
        bytes_since_cancellation_check += read as u64;
        if should_check_for_cancellation(bytes_since_cancellation_check) {
            ensure_transfer_not_canceled(cancellation_marker_path)?;
            bytes_since_cancellation_check = 0;
        }
        progress.emit(bytes_done, total_bytes, current_path);
    }

    Ok(())
}

fn ensure_transfer_not_canceled(cancellation_marker_path: Option<&str>) -> anyhow::Result<()> {
    if let Some(path) = cancellation_marker_path {
        if Path::new(path).exists() {
            return Err(anyhow::Error::new(NativeSftpTransferError::new(
                NativeSftpTransferErrorKind::Canceled,
                "Transfer canceled.",
            )));
        }
    }

    Ok(())
}

fn should_check_for_cancellation(bytes_since_last_check: u64) -> bool {
    bytes_since_last_check >= CANCELLATION_CHECK_INTERVAL_BYTES
}

fn classify_sftp_transfer_error(error: &anyhow::Error) -> (c_int, &'static str, String) {
    if let Some(native_error) = error.downcast_ref::<NativeSftpTransferError>() {
        return match native_error.kind {
            NativeSftpTransferErrorKind::Canceled => (
                NOVA_SSH_RESULT_CANCELED,
                "canceled",
                native_error.message.clone(),
            ),
            NativeSftpTransferErrorKind::NotImplemented => (
                NOVA_SSH_RESULT_NOT_IMPLEMENTED,
                "not-implemented",
                native_error.message.clone(),
            ),
            NativeSftpTransferErrorKind::InvalidArgument => (
                NOVA_SSH_RESULT_INVALID_ARGUMENT,
                "invalid-argument",
                native_error.message.clone(),
            ),
            NativeSftpTransferErrorKind::RemotePathNotFound
            | NativeSftpTransferErrorKind::LocalPathNotFound
            | NativeSftpTransferErrorKind::PermissionDenied => (
                NOVA_SSH_RESULT_CLOSED,
                "error",
                native_error.message.clone(),
            ),
        };
    }

    (NOVA_SSH_RESULT_CLOSED, "error", error.to_string())
}

fn map_remote_transfer_error<E>(path: &str, error: E) -> anyhow::Error
where
    E: std::fmt::Display,
{
    let message = error.to_string();
    let lower = message.to_ascii_lowercase();
    if lower.contains("permission denied") {
        return anyhow::Error::new(NativeSftpTransferError::new(
            NativeSftpTransferErrorKind::PermissionDenied,
            format!("Permission denied: {}", path),
        ));
    }

    if lower.contains("no such file") || lower.contains("not found") {
        return anyhow::Error::new(NativeSftpTransferError::new(
            NativeSftpTransferErrorKind::RemotePathNotFound,
            format!("Remote path not found: {}", path),
        ));
    }

    anyhow::anyhow!(message)
}

fn map_local_transfer_error(path: &Path, error: std::io::Error) -> anyhow::Error {
    match error.kind() {
        std::io::ErrorKind::NotFound => anyhow::Error::new(NativeSftpTransferError::new(
            NativeSftpTransferErrorKind::LocalPathNotFound,
            format!("Local path not found: {}", path.display()),
        )),
        std::io::ErrorKind::PermissionDenied => anyhow::Error::new(NativeSftpTransferError::new(
            NativeSftpTransferErrorKind::PermissionDenied,
            format!("Permission denied: {}", path.display()),
        )),
        _ => anyhow::anyhow!(error),
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum NativeSftpTransferErrorKind {
    Canceled,
    NotImplemented,
    InvalidArgument,
    RemotePathNotFound,
    LocalPathNotFound,
    PermissionDenied,
}

#[derive(Debug)]
struct NativeSftpTransferError {
    kind: NativeSftpTransferErrorKind,
    message: String,
}

impl NativeSftpTransferError {
    fn new(kind: NativeSftpTransferErrorKind, message: impl Into<String>) -> Self {
        Self {
            kind,
            message: message.into(),
        }
    }
}

impl std::fmt::Display for NativeSftpTransferError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for NativeSftpTransferError {}

fn remote_basename(path: &str) -> anyhow::Result<String> {
    let normalized = normalize_remote_directory_path(path)?;
    let basename = normalized
        .rsplit('/')
        .find(|segment| !segment.is_empty())
        .map(str::to_owned)
        .ok_or_else(|| {
            anyhow::anyhow!("Remote directory name is required for native SFTP transfer.")
        })?;

    // A remote path ending in `/..` (or `/.`) would otherwise yield a basename of
    // ".." and relocate the download root a level above the directory the user
    // chose. User-supplied rather than server-supplied, so this is hygiene rather
    // than the vulnerability fixed in validate_remote_entry_name — but the root
    // still has to land where the user pointed it.
    if basename == "." || basename == ".." {
        return Err(anyhow::Error::new(NativeSftpTransferError::new(
            NativeSftpTransferErrorKind::InvalidArgument,
            format!(
                "Remote directory path '{path}' resolves to '{basename}' and cannot be \
                 used as a download directory name."
            ),
        )));
    }

    Ok(basename)
}

fn resolve_upload_file_destination_path(
    local_name: &str,
    remote_path: &str,
    home_directory: Option<&str>,
    remote_path_is_dir: bool,
) -> anyhow::Result<String> {
    let trimmed_remote_path = remote_path.trim();
    let expanded_remote_path = expand_remote_home_path(remote_path, home_directory)?;
    if trimmed_remote_path == "~" || remote_path_is_dir {
        return Ok(join_remote_path(&expanded_remote_path, local_name));
    }

    Ok(expanded_remote_path)
}

fn expand_remote_home_path(path: &str, home_directory: Option<&str>) -> anyhow::Result<String> {
    let trimmed = path.trim();
    if trimmed.is_empty() {
        return Err(anyhow::Error::new(NativeSftpTransferError::new(
            NativeSftpTransferErrorKind::InvalidArgument,
            "Remote file path is required for native SFTP transfer.",
        )));
    }

    if trimmed == "~" {
        return normalize_remote_directory_path(home_directory.ok_or_else(|| {
            anyhow::Error::new(NativeSftpTransferError::new(
                NativeSftpTransferErrorKind::InvalidArgument,
                "Remote home directory is unavailable for native SFTP upload.",
            ))
        })?);
    }

    if let Some(relative_path) = trimmed.strip_prefix("~/") {
        let home_directory = normalize_remote_directory_path(home_directory.ok_or_else(|| {
            anyhow::Error::new(NativeSftpTransferError::new(
                NativeSftpTransferErrorKind::InvalidArgument,
                "Remote home directory is unavailable for native SFTP upload.",
            ))
        })?)?;
        return Ok(join_remote_path(&home_directory, relative_path));
    }

    Ok(trimmed.to_owned())
}

fn normalize_remote_directory_path(path: &str) -> anyhow::Result<String> {
    let trimmed = path.trim().trim_end_matches('/');
    if trimmed.is_empty() {
        return Ok("/".to_owned());
    }

    if trimmed == "." || trimmed == ".." {
        return Err(anyhow::Error::new(NativeSftpTransferError::new(
            NativeSftpTransferErrorKind::InvalidArgument,
            format!("Remote directory path '{trimmed}' is not supported for native SFTP transfer."),
        )));
    }

    Ok(trimmed.to_owned())
}

/// Suffix used for in-progress downloads so a cancelled or failed transfer never
/// leaves a truncated file at the destination path.
const PARTIAL_DOWNLOAD_SUFFIX: &str = ".novapart";

/// Validates a **server-supplied** directory entry name before it is joined onto a
/// local path.
///
/// Entry names returned by `read_dir` are attacker-controlled: a malicious or
/// compromised server chooses them. `Path::join` with an absolute component silently
/// *replaces* the base rather than nesting under it, so an unchecked join turns a
/// directory download into an arbitrary file write anywhere the process can reach.
///
/// A legitimate entry name is exactly one normal path component. Requiring that (and
/// that the component round-trips to the original string) rejects every escape shape
/// at once: `..`, `.`, empty, `/` or `\` separators, absolute and drive-relative
/// paths (`/etc/cron.d/x`, `C:\Windows\x`, `C:x`), UNC prefixes, and interior NULs.
fn validate_remote_entry_name(file_name: &str) -> anyhow::Result<()> {
    let rejected = |reason: &str| {
        anyhow::Error::new(NativeSftpTransferError::new(
            NativeSftpTransferErrorKind::InvalidArgument,
            format!(
                "Remote server returned an unsafe directory entry name {file_name:?} \
                 ({reason}). Refusing to write outside the download directory."
            ),
        ))
    };

    if file_name.is_empty() {
        return Err(rejected("empty"));
    }

    if file_name.contains('\0') {
        return Err(rejected("contains a NUL byte"));
    }

    // Reject both separators regardless of host platform: a name containing '\' is
    // harmless on Unix but escapes on Windows, and the same server may serve both.
    if file_name.contains('/') || file_name.contains('\\') {
        return Err(rejected("contains a path separator"));
    }

    let mut components = Path::new(file_name).components();
    match (components.next(), components.next()) {
        (Some(Component::Normal(value)), None) if value == OsStr::new(file_name) => Ok(()),
        _ => Err(rejected("is not a single relative path component")),
    }
}

/// Defence in depth for [`validate_remote_entry_name`]: confirms a joined child path
/// is still lexically inside the download root. Purely a guard against a future
/// refactor reintroducing an unvalidated join — with name validation in place this
/// never fires.
fn ensure_within_download_root(local_root: &Path, candidate: &Path) -> anyhow::Result<()> {
    if candidate.starts_with(local_root) {
        return Ok(());
    }

    Err(anyhow::Error::new(NativeSftpTransferError::new(
        NativeSftpTransferErrorKind::InvalidArgument,
        format!(
            "Refusing to write {} outside the download directory {}.",
            candidate.display(),
            local_root.display()
        ),
    )))
}

/// Monotonic discriminator for partial-download file names. Combined with the process
/// id it keeps concurrent transfers - which `SftpService` runs on independent
/// `Task.Run` jobs, including two jobs targeting the same destination - from ever
/// sharing a scratch file.
static PARTIAL_DOWNLOAD_COUNTER: AtomicU64 = AtomicU64::new(0);

/// Destination for the in-progress copy: the final name plus a per-transfer unique
/// discriminator and [`PARTIAL_DOWNLOAD_SUFFIX`], so the previous good copy at
/// `local_path` survives a cancelled or failed transfer untouched.
///
/// The name must be unique rather than deterministic: two concurrent downloads to the
/// same destination would otherwise open and truncate the same scratch file, interleave
/// their writes, and each rename mismatched content into place while reporting success.
fn partial_download_path(local_path: &Path) -> PathBuf {
    let mut file_name = local_path
        .file_name()
        .map(OsString::from)
        .unwrap_or_else(|| OsString::from("download"));
    file_name.push(format!(
        ".{}.{}{}",
        std::process::id(),
        PARTIAL_DOWNLOAD_COUNTER.fetch_add(1, Ordering::Relaxed),
        PARTIAL_DOWNLOAD_SUFFIX
    ));
    local_path.with_file_name(file_name)
}

/// Creates the scratch file for an in-progress download.
///
/// Uses `create_new` (`O_EXCL` / `CREATE_NEW`) rather than `create`. Beyond catching a
/// name collision, this is what makes the write safe in a destination directory another
/// local actor can write to: `create` follows symlinks, so a pre-created
/// `<destination>.<pid>.<n>.novapart` symlink would redirect the downloaded bytes into
/// whatever it points at. `O_EXCL` refuses to open an existing path at all, symlink or
/// not, so a planted link fails the transfer instead of being followed.
async fn create_partial_download_file(partial_path: &Path) -> anyhow::Result<TokioFile> {
    tokio::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(partial_path)
        .await
        .map_err(|error| map_local_transfer_error(partial_path, error))
}

/// Best-effort removal of an abandoned partial download. Failures are ignored
/// deliberately: the caller is already returning the original transfer error, and a
/// leftover `.novapart` file is strictly less harmful than masking that error.
async fn discard_partial_download(partial_path: &Path) {
    let _ = tokio::fs::remove_file(partial_path).await;
}

fn join_remote_path(base: &str, child: &str) -> String {
    let trimmed_base = base.trim_end_matches('/');
    let trimmed_child = child.trim_matches('/');
    if trimmed_base.is_empty() || trimmed_base == "/" {
        format!("/{}", trimmed_child)
    } else {
        format!("{}/{}", trimmed_base, trimmed_child)
    }
}

/// Upper bound for the raw TCP connect (incl. DNS resolution). Deliberately applies
/// only to the socket phase — the SSH handshake and auth can involve user prompts
/// (host-key decision, password) and are cancelled via SharedState::wait_closed
/// instead of a wall-clock timeout.
const TCP_CONNECT_TIMEOUT: Duration = Duration::from_secs(30);

async fn connect_tcp_with_timeout(host: &str, port: u16) -> anyhow::Result<tokio::net::TcpStream> {
    match tokio::time::timeout(
        TCP_CONNECT_TIMEOUT,
        tokio::net::TcpStream::connect((host, port)),
    )
    .await
    {
        Ok(Ok(stream)) => Ok(stream),
        Ok(Err(error)) => Err(anyhow::anyhow!(
            "TCP connect to {host}:{port} failed: {error}"
        )),
        Err(_) => Err(anyhow::anyhow!(
            "TCP connect to {host}:{port} timed out after {}s",
            TCP_CONNECT_TIMEOUT.as_secs()
        )),
    }
}

/// Establishes the SSH session up to a ready shell channel: optional jump hop,
/// TCP connect (bounded by TCP_CONNECT_TIMEOUT), handshake, auth, shell detection,
/// PTY + shell/exec setup. Runs inside run_session's select! race against
/// SharedState::wait_closed, so it must not consume `command_rx`. The jump handle is
/// returned so the tunnel outlives establishment.
async fn establish_session(
    config: &ConnectConfig,
    shared: &Arc<SharedState>,
    client_config: Arc<client::Config>,
) -> anyhow::Result<(
    Option<client::Handle<NovaClientHandler>>,
    client::Handle<NovaClientHandler>,
    russh::Channel<client::Msg>,
)> {
    let jump_session = if let Some(jump_host) = &config.jump_host {
        let jump_handler = NovaClientHandler {
            shared: shared.clone(),
            host: jump_host.host.clone(),
            port: jump_host.port,
        };

        let stream = connect_tcp_with_timeout(jump_host.host.as_str(), jump_host.port).await?;
        let mut jump = client::connect_stream(client_config.clone(), stream, jump_handler).await?;

        authenticate(
            &jump_host.user,
            config.identity_file.as_deref(),
            shared,
            &mut jump,
        )
        .await?;
        Some(jump)
    } else {
        None
    };

    let handler = NovaClientHandler {
        shared: shared.clone(),
        host: config.host.clone(),
        port: config.port,
    };

    let mut session = if let Some(jump) = &jump_session {
        // This channel open IS the target-side TCP connect (performed by the jump
        // server), so it needs the same bound as a direct connect: a blackholed
        // target would otherwise hang until the remote sshd gives up.
        let stream = tokio::time::timeout(
            TCP_CONNECT_TIMEOUT,
            jump.channel_open_direct_tcpip(config.host.clone(), config.port as u32, "127.0.0.1", 0),
        )
        .await
        .map_err(|_| {
            anyhow::anyhow!(
                "direct-tcpip open to {}:{} via jump host timed out after {}s",
                config.host,
                config.port,
                TCP_CONNECT_TIMEOUT.as_secs()
            )
        })??
        .into_stream();

        client::connect_stream(client_config.clone(), stream, handler).await?
    } else {
        let stream = connect_tcp_with_timeout(config.host.as_str(), config.port).await?;
        client::connect_stream(client_config.clone(), stream, handler).await?
    };

    authenticate(
        &config.user,
        config.identity_file.as_deref(),
        shared,
        &mut session,
    )
    .await?;

    let effective_shell_kind = if config.remote_shell_kind != RemoteShellKind::Auto {
        config.remote_shell_kind
    } else if let Some(command) = config.shell_detection_command.as_deref() {
        match detect_login_shell(&mut session, command).await {
            Ok(shell_kind) => shell_kind,
            Err(_) => RemoteShellKind::Auto,
        }
    } else {
        RemoteShellKind::Auto
    };

    let mut channel = session.channel_open_session().await?;
    channel
        .request_pty(
            true,
            &config.term,
            config.cols as u32,
            config.rows as u32,
            0,
            0,
            &[],
        )
        .await?;
    if let Some(startup_command) = build_startup_command(effective_shell_kind, config) {
        channel.exec(true, startup_command).await?;
    } else {
        channel.request_shell(true).await?;
    }

    Ok((jump_session, session, channel))
}

fn run_session(
    config: ConnectConfig,
    shared: Arc<SharedState>,
    mut command_rx: mpsc::UnboundedReceiver<WorkerCommand>,
) -> anyhow::Result<()> {
    let runtime = Builder::new_current_thread().enable_all().build()?;
    runtime.block_on(async move {
        let forward_channels = Arc::new(tokio::sync::Mutex::new(HashMap::new()));
        let client_config = Arc::new(build_client_config(&config));

        // Session establishment (TCP connect, handshake, auth, shell setup) used to run
        // unguarded: nova_ssh_close's WorkerCommand::Close is only consumed by the main
        // select loop below, so a connect stuck on a dead host made `worker.join()` hang
        // — potentially on the .NET finalizer thread (#155). Race the whole phase
        // against mark_closed(), and bound the raw TCP connects with a timeout. The
        // handshake/auth legs deliberately carry no timeout of their own: they can block
        // on user interaction (host-key and password prompts), and mark_closed already
        // unblocks those via wait_for_response.
        let (jump_session, mut session, mut channel) = tokio::select! {
            result = establish_session(&config, &shared, client_config.clone()) => result?,
            _ = shared.wait_closed() => {
                // Closed while connecting: exit cleanly; nova_ssh_close is joining us.
                return Ok(());
            }
        };

        shared.queue_event(QueuedEvent {
            kind: NovaSshEventKind::Connected,
            payload: serde_json::to_vec(&ConnectedPayload {
                host: &config.host,
                port: config.port,
                user: &config.user,
            })?,
            status_code: 0,
            flags: NOVA_SSH_EVENT_FLAG_JSON,
        });

        let mut pending_command: Option<WorkerCommand> = None;
        loop {
            tokio::select! {
                command = next_worker_command(&mut pending_command, &mut command_rx) => {
                    match command {
                        Some(WorkerCommand::Write(data)) => {
                            channel.data(&data[..]).await?;
                        }
                        Some(WorkerCommand::Resize { cols, rows }) => {
                            let (cols, rows, pending_resize_command) = coalesce_pending_resize_commands(
                                &mut command_rx,
                                cols,
                                rows,
                            );
                            pending_command = pending_resize_command;

                            channel.window_change(cols as u32, rows as u32, 0, 0).await?;
                        }
                        Some(WorkerCommand::OpenDirectTcpIp {
                            host_to_connect,
                            port_to_connect,
                            originator_address,
                            originator_port,
                            reply,
                        }) => {
                            let result = open_direct_tcpip_channel(
                                &session,
                                forward_channels.clone(),
                                shared.clone(),
                                host_to_connect,
                                port_to_connect,
                                originator_address,
                                originator_port,
                            )
                            .await;
                            let _ = reply.send(result);
                        }
                        Some(WorkerCommand::WriteForwardChannel { channel_id, data }) => {
                            write_forward_channel(forward_channels.clone(), channel_id, data).await?;
                        }
                        Some(WorkerCommand::ForwardChannelEof { channel_id }) => {
                            send_forward_channel_eof(forward_channels.clone(), channel_id).await?;
                        }
                        Some(WorkerCommand::CloseForwardChannel { channel_id }) => {
                            close_forward_channel(forward_channels.clone(), channel_id).await?;
                        }
                        Some(WorkerCommand::Close) | None => {
                            close_all_forward_channels(forward_channels.clone()).await;
                            let _ = channel.eof().await;
                            let _ = channel.close().await;
                            break;
                        }
                    }
                }
                message = channel.wait() => {
                    match message {
                        Some(ChannelMsg::Data { data }) => {
                            shared.queue_event(QueuedEvent {
                                kind: NovaSshEventKind::Data,
                                payload: data.to_vec(),
                                status_code: 0,
                                flags: NOVA_SSH_EVENT_FLAG_BINARY,
                            });
                        }
                        Some(ChannelMsg::ExtendedData { data, .. }) => {
                            shared.queue_event(QueuedEvent {
                                kind: NovaSshEventKind::Data,
                                payload: data.to_vec(),
                                status_code: 0,
                                flags: NOVA_SSH_EVENT_FLAG_BINARY,
                            });
                        }
                        Some(ChannelMsg::ExitStatus { exit_status }) => {
                            shared.queue_event(QueuedEvent {
                                kind: NovaSshEventKind::ExitStatus,
                                payload: serde_json::to_vec(&ExitStatusPayload { exit_status })?,
                                status_code: exit_status as i32,
                                flags: NOVA_SSH_EVENT_FLAG_JSON,
                            });
                        }
                        Some(ChannelMsg::Eof) | Some(ChannelMsg::Close) | None => {
                            break;
                        }
                        _ => {}
                    }
                }
            }
        }

        let _ = session
            .disconnect(Disconnect::ByApplication, "Closed by NovaTerminal", "en")
            .await;
        if let Some(jump) = jump_session {
            let _ = jump
                .disconnect(Disconnect::ByApplication, "Closed by NovaTerminal", "en")
                .await;
        }
        Ok(())
    })
}

async fn next_worker_command(
    pending_command: &mut Option<WorkerCommand>,
    command_rx: &mut mpsc::UnboundedReceiver<WorkerCommand>,
) -> Option<WorkerCommand> {
    if let Some(command) = pending_command.take() {
        return Some(command);
    }

    command_rx.recv().await
}

fn coalesce_pending_resize_commands(
    command_rx: &mut mpsc::UnboundedReceiver<WorkerCommand>,
    mut cols: u16,
    mut rows: u16,
) -> (u16, u16, Option<WorkerCommand>) {
    let mut pending_command = None;

    loop {
        match command_rx.try_recv() {
            Ok(WorkerCommand::Resize {
                cols: next_cols,
                rows: next_rows,
            }) => {
                cols = next_cols;
                rows = next_rows;
            }
            Ok(command) => {
                pending_command = Some(command);
                break;
            }
            Err(mpsc::error::TryRecvError::Empty)
            | Err(mpsc::error::TryRecvError::Disconnected) => {
                break;
            }
        }
    }

    (cols, rows, pending_command)
}

async fn open_direct_tcpip_channel(
    session: &client::Handle<NovaClientHandler>,
    forward_channels: Arc<
        tokio::sync::Mutex<HashMap<u32, Arc<russh::ChannelWriteHalf<client::Msg>>>>,
    >,
    shared: Arc<SharedState>,
    host_to_connect: String,
    port_to_connect: u32,
    originator_address: String,
    originator_port: u32,
) -> anyhow::Result<u32> {
    let channel = session
        .channel_open_direct_tcpip(
            host_to_connect,
            port_to_connect,
            originator_address,
            originator_port,
        )
        .await?;

    let channel_id = u32::from(channel.id());
    let (mut read_half, write_half) = channel.split();
    forward_channels
        .lock()
        .await
        .insert(channel_id, Arc::new(write_half));

    let reader_shared = shared.clone();
    let reader_channels = forward_channels.clone();
    tokio::spawn(async move {
        loop {
            match read_half.wait().await {
                Some(ChannelMsg::Data { data }) => {
                    reader_shared.queue_event(QueuedEvent {
                        kind: NovaSshEventKind::ForwardChannelData,
                        payload: data.to_vec(),
                        status_code: channel_id as i32,
                        flags: NOVA_SSH_EVENT_FLAG_BINARY,
                    });
                }
                Some(ChannelMsg::ExtendedData { data, .. }) => {
                    reader_shared.queue_event(QueuedEvent {
                        kind: NovaSshEventKind::ForwardChannelData,
                        payload: data.to_vec(),
                        status_code: channel_id as i32,
                        flags: NOVA_SSH_EVENT_FLAG_BINARY,
                    });
                }
                Some(ChannelMsg::Eof) => {
                    reader_shared.queue_event(QueuedEvent {
                        kind: NovaSshEventKind::ForwardChannelEof,
                        payload: Vec::new(),
                        status_code: channel_id as i32,
                        flags: NOVA_SSH_EVENT_FLAG_JSON,
                    });
                }
                Some(ChannelMsg::Close) | None => {
                    reader_channels.lock().await.remove(&channel_id);
                    reader_shared.queue_event(QueuedEvent {
                        kind: NovaSshEventKind::ForwardChannelClosed,
                        payload: Vec::new(),
                        status_code: channel_id as i32,
                        flags: NOVA_SSH_EVENT_FLAG_JSON,
                    });
                    break;
                }
                _ => {}
            }
        }
    });

    Ok(channel_id)
}

async fn write_forward_channel(
    forward_channels: Arc<
        tokio::sync::Mutex<HashMap<u32, Arc<russh::ChannelWriteHalf<client::Msg>>>>,
    >,
    channel_id: u32,
    data: Vec<u8>,
) -> anyhow::Result<()> {
    let writer = {
        let channels = forward_channels.lock().await;
        channels.get(&channel_id).cloned()
    };

    if let Some(writer) = writer {
        writer.data(Cursor::new(data)).await?;
    }

    Ok(())
}

async fn send_forward_channel_eof(
    forward_channels: Arc<
        tokio::sync::Mutex<HashMap<u32, Arc<russh::ChannelWriteHalf<client::Msg>>>>,
    >,
    channel_id: u32,
) -> anyhow::Result<()> {
    let writer = {
        let channels = forward_channels.lock().await;
        channels.get(&channel_id).cloned()
    };

    if let Some(writer) = writer {
        writer.eof().await?;
    }

    Ok(())
}

async fn close_forward_channel(
    forward_channels: Arc<
        tokio::sync::Mutex<HashMap<u32, Arc<russh::ChannelWriteHalf<client::Msg>>>>,
    >,
    channel_id: u32,
) -> anyhow::Result<()> {
    let writer = forward_channels.lock().await.remove(&channel_id);
    if let Some(writer) = writer {
        writer.close().await?;
    }

    Ok(())
}

async fn close_all_forward_channels(
    forward_channels: Arc<
        tokio::sync::Mutex<HashMap<u32, Arc<russh::ChannelWriteHalf<client::Msg>>>>,
    >,
) {
    let writers = {
        let mut channels = forward_channels.lock().await;
        channels
            .drain()
            .map(|(_, writer)| writer)
            .collect::<Vec<_>>()
    };

    for writer in writers {
        let _ = writer.close().await;
    }
}

async fn authenticate(
    user: &str,
    identity_file: Option<&str>,
    shared: &Arc<SharedState>,
    session: &mut client::Handle<NovaClientHandler>,
) -> anyhow::Result<()> {
    if let Some(identity_file) = identity_file {
        if let Some(auth_result) = try_public_key_auth(user, shared, session, identity_file).await?
        {
            if auth_result.success() {
                return Ok(());
            }
        }
    }

    let password = prompt_text(
        shared,
        NovaSshEventKind::PasswordPrompt,
        "Password:",
        NovaSshResponseKind::Password,
    )?;
    // As in authenticate_transfer: russh needs an owned String; our copy is wiped when
    // `password` drops at the end of this function.
    let password_auth = session
        .authenticate_password(user.to_owned(), password.as_str().to_owned())
        .await?;
    if password_auth.success() {
        return Ok(());
    }

    let keyboard_auth = authenticate_keyboard_interactive(user, shared, session).await?;
    if keyboard_auth {
        return Ok(());
    }

    anyhow::bail!("SSH authentication failed")
}

async fn try_public_key_auth(
    user: &str,
    shared: &Arc<SharedState>,
    session: &mut client::Handle<NovaClientHandler>,
    identity_file: &str,
) -> anyhow::Result<Option<AuthResult>> {
    let key = match load_secret_key(Path::new(identity_file), None) {
        Ok(key) => key,
        Err(_) => {
            let passphrase = prompt_text(
                shared,
                NovaSshEventKind::PassphrasePrompt,
                "Key passphrase:",
                NovaSshResponseKind::Passphrase,
            )?;
            load_secret_key(Path::new(identity_file), Some(passphrase.as_str()))?
        }
    };

    let hash_alg = session.best_supported_rsa_hash().await?.flatten();
    let auth = session
        .authenticate_publickey(
            user.to_owned(),
            PrivateKeyWithHashAlg::new(Arc::new(key), hash_alg),
        )
        .await?;
    Ok(Some(auth))
}

async fn authenticate_keyboard_interactive(
    user: &str,
    shared: &Arc<SharedState>,
    session: &mut client::Handle<NovaClientHandler>,
) -> anyhow::Result<bool> {
    let mut response = session
        .authenticate_keyboard_interactive_start(user.to_owned(), None::<String>)
        .await?;

    loop {
        match response {
            KeyboardInteractiveAuthResponse::Success => return Ok(true),
            KeyboardInteractiveAuthResponse::Failure { .. } => return Ok(false),
            KeyboardInteractiveAuthResponse::InfoRequest {
                name,
                instructions,
                prompts,
            } => {
                let payload = KeyboardInteractivePromptPayload {
                    name,
                    instructions,
                    prompts: prompts
                        .into_iter()
                        .map(|prompt| KeyboardPromptPayload {
                            prompt: prompt.prompt,
                            echo: prompt.echo,
                        })
                        .collect(),
                };

                shared.queue_event(QueuedEvent {
                    kind: NovaSshEventKind::KeyboardInteractivePrompt,
                    payload: serde_json::to_vec(&payload)?,
                    status_code: 0,
                    flags: NOVA_SSH_EVENT_FLAG_JSON,
                });

                let responses = wait_keyboard_responses(shared)?;
                response = session
                    .authenticate_keyboard_interactive_respond(responses.to_vec())
                    .await?;
            }
        }
    }
}

fn prompt_text(
    shared: &Arc<SharedState>,
    event_kind: NovaSshEventKind,
    prompt: &str,
    response_kind: NovaSshResponseKind,
) -> anyhow::Result<Zeroizing<String>> {
    shared.queue_event(QueuedEvent {
        kind: event_kind,
        payload: serde_json::to_vec(&TextPromptPayload { prompt })?,
        status_code: 0,
        flags: NOVA_SSH_EVENT_FLAG_JSON,
    });

    // The raw response payload is JSON containing the secret in cleartext
    // (`{"text":"..."}`), so the buffer itself has to be wiped, not just the parsed
    // string.
    let payload = Zeroizing::new(
        shared
            .wait_for_response(response_kind)
            .ok_or_else(|| anyhow::anyhow!("SSH prompt canceled"))?,
    );
    let response = serde_json::from_slice::<TextResponse>(&payload)?;
    Ok(Zeroizing::new(response.text))
}

fn wait_keyboard_responses(
    shared: &Arc<SharedState>,
) -> anyhow::Result<Zeroizing<Vec<String>>> {
    // Keyboard-interactive answers are credentials too - same treatment as prompt_text.
    let payload = Zeroizing::new(
        shared
            .wait_for_response(NovaSshResponseKind::KeyboardInteractive)
            .ok_or_else(|| anyhow::anyhow!("Keyboard-interactive prompt canceled"))?,
    );
    let response = serde_json::from_slice::<KeyboardInteractiveResponse>(&payload)?;
    Ok(Zeroizing::new(response.responses))
}

#[cfg(test)]
fn create_test_session_with_event(kind: NovaSshEventKind, payload: &[u8]) -> usize {
    let shared = Arc::new(SharedState::new());
    shared.queue_event(QueuedEvent {
        kind,
        payload: payload.to_vec(),
        status_code: 0,
        flags: NOVA_SSH_EVENT_FLAG_JSON,
    });

    registry_insert(NovaSshSession {
        shared,
        command_tx: Mutex::new(None),
        worker: Mutex::new(None),
    }) as usize
}

#[cfg(test)]
mod tests {
    use super::*;

    // #155: session establishment races against wait_closed so nova_ssh_close can
    // abort a stuck connect instead of hanging worker.join() (and the .NET
    // finalizer thread). These pin the notify semantics that race depends on.
    #[test]
    fn wait_closed_resolves_after_mark_closed_from_another_thread() {
        let shared = Arc::new(SharedState::new());
        let closer = shared.clone();
        let handle = thread::spawn(move || {
            thread::sleep(Duration::from_millis(50));
            closer.mark_closed();
        });

        let runtime = Builder::new_current_thread().enable_all().build().unwrap();
        runtime.block_on(async {
            tokio::time::timeout(Duration::from_secs(5), shared.wait_closed())
                .await
                .expect("wait_closed must resolve after mark_closed");
        });
        handle.join().unwrap();
    }

    #[test]
    fn wait_closed_resolves_immediately_when_already_closed() {
        let shared = SharedState::new();
        shared.mark_closed();

        let runtime = Builder::new_current_thread().enable_all().build().unwrap();
        runtime.block_on(async {
            tokio::time::timeout(Duration::from_millis(100), shared.wait_closed())
                .await
                .expect("wait_closed must resolve without any notification when already closed");
        });
    }

    #[test]
    fn tcp_connect_timeout_reports_refused_connection_promptly() {
        let runtime = Builder::new_current_thread().enable_all().build().unwrap();
        runtime.block_on(async {
            // Bind-then-drop gives a port with (almost certainly) no listener.
            let port = {
                let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
                listener.local_addr().unwrap().port()
            };
            let result = connect_tcp_with_timeout("127.0.0.1", port).await;
            assert!(result.is_err(), "connect to a closed port must fail");
        });
    }

    #[test]
    fn null_handle_operations_return_invalid_argument() {
        let resize = nova_ssh_resize(0, 120, 30);
        let write = nova_ssh_write(0, [1u8, 2, 3].as_ptr(), 3);
        let forward_args = NovaSshDirectTcpIpArgs {
            host_to_connect: ptr::null(),
            port_to_connect: 80,
            originator_address: ptr::null(),
            originator_port: 1000,
        };
        let open = nova_ssh_open_direct_tcpip(0, &forward_args);
        let channel_write = nova_ssh_channel_write(0, 1, [1u8, 2, 3].as_ptr(), 3);
        let channel_eof = nova_ssh_channel_eof(0, 1);
        let channel_close = nova_ssh_channel_close(0, 1);
        let respond = nova_ssh_submit_response(0, 1, br#"{}"#.as_ptr(), 2);
        let mut sftp_response = ptr::null_mut();
        let sftp = nova_ssh_sftp_transfer(ptr::null(), None, ptr::null_mut(), &mut sftp_response);
        let close = nova_ssh_close(0);

        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, resize);
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, write);
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, open);
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, channel_write);
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, channel_eof);
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, channel_close);
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, respond);
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, sftp);
        assert!(sftp_response.is_null());
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, close);
    }

    #[test]
    fn sftp_transfer_rejects_unsupported_modes() {
        let request = CString::new(
            r#"{"connection":{"host":"example.com","user":"nova","port":22,"password":"secret","knownHostsFilePath":"known_hosts.json"},"transfer":{"direction":"sync","kind":"directory","localPath":"local.txt","remotePath":"/tmp/remote.txt"}}"#,
        )
        .unwrap();
        let mut response = ptr::null_mut();

        let rc = nova_ssh_sftp_transfer(request.as_ptr(), None, ptr::null_mut(), &mut response);

        assert_eq!(NOVA_SSH_RESULT_NOT_IMPLEMENTED, rc);
        assert!(!response.is_null());

        let response_json = unsafe { CStr::from_ptr(response) }.to_str().unwrap();
        let payload: serde_json::Value = serde_json::from_str(response_json).unwrap();
        assert_eq!("not-implemented", payload["status"]);
        assert_eq!(
            "Native SFTP transfer mode 'sync/directory' is not implemented yet.",
            payload["message"]
        );

        nova_ssh_string_free(response);
    }

    #[test]
    fn sftp_list_directory_rejects_incomplete_requests() {
        let request = CString::new(
            r#"{"connection":{"host":"example.com","user":"nova","port":22,"password":"secret","knownHostsFilePath":"known_hosts.json"},"path":""}"#,
        )
        .unwrap();
        let mut response = ptr::null_mut();

        let rc = nova_ssh_sftp_list_directory(request.as_ptr(), &mut response);

        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, rc);
        assert!(!response.is_null());

        let response_json = unsafe { CStr::from_ptr(response) }.to_str().unwrap();
        let payload: serde_json::Value = serde_json::from_str(response_json).unwrap();
        assert_eq!("invalid-argument", payload["status"]);
        assert!(
            payload["message"]
                .as_str()
                .unwrap_or_default()
                .contains("incomplete")
        );

        nova_ssh_string_free(response);
    }

    #[test]
    fn remote_path_list_response_serializes_modified_unix_seconds() {
        let mut response = ptr::null_mut();
        let entries = vec![RemotePathListEntry {
            name: "access.log".to_owned(),
            full_path: "/srv/access.log".to_owned(),
            is_directory: false,
            modified_at_unix_seconds: Some(1_777_925_700),
        }];

        let rc = write_remote_path_list_response_json(
            &mut response,
            NOVA_SSH_RESULT_OK,
            "ok",
            "listed",
            entries,
        );

        assert_eq!(NOVA_SSH_RESULT_OK, rc);
        assert!(!response.is_null());

        let response_json = unsafe { CStr::from_ptr(response) }.to_str().unwrap();
        let payload: serde_json::Value = serde_json::from_str(response_json).unwrap();
        assert_eq!(1_777_925_700u64, payload["entries"][0]["modifiedAtUnixSeconds"]);

        nova_ssh_string_free(response);
    }

    #[test]
    fn poll_reports_required_payload_length_before_copying() {
        let payload = br#"{"host":"example.internal","fingerprint":"SHA256:test"}"#;
        let session = create_test_session_with_event(NovaSshEventKind::HostKeyPrompt, payload);
        assert_ne!(0, session);

        let mut event = NovaSshEvent::default();
        let mut tiny = [0u8; 8];
        let rc = nova_ssh_poll_event(session, &mut event, tiny.as_mut_ptr(), tiny.len());

        assert_eq!(NOVA_SSH_RESULT_BUFFER_TOO_SMALL, rc);
        assert_eq!(NovaSshEventKind::HostKeyPrompt as u32, event.kind);
        assert_eq!(payload.len() as u32, event.payload_len);

        let close = nova_ssh_close(session);
        assert_eq!(NOVA_SSH_RESULT_OK, close);
    }

    // The refactor in #173 item 1 turned peek-then-pop into a single take-if-fits, so the retry after
    // BUFFER_TOO_SMALL is the path most at risk of losing or corrupting a payload. Nothing covered it
    // before: the test above stops at the size report.
    #[test]
    fn retry_after_buffer_too_small_delivers_the_payload_intact() {
        let payload = br#"{"host":"example.internal","fingerprint":"SHA256:retry-path"}"#;
        let session = create_test_session_with_event(NovaSshEventKind::HostKeyPrompt, payload);
        assert_ne!(0, session);

        let mut event = NovaSshEvent::default();
        let mut tiny = [0u8; 4];
        assert_eq!(
            NOVA_SSH_RESULT_BUFFER_TOO_SMALL,
            nova_ssh_poll_event(session, &mut event, tiny.as_mut_ptr(), tiny.len())
        );

        // Size from the header the failed poll just wrote, exactly as the managed caller does.
        let mut buffer = vec![0u8; event.payload_len as usize];
        assert_eq!(
            NOVA_SSH_RESULT_EVENT_READY,
            nova_ssh_poll_event(session, &mut event, buffer.as_mut_ptr(), buffer.len())
        );

        assert_eq!(payload.len() as u32, event.payload_len);
        assert_eq!(payload.as_slice(), &buffer[..], "payload came back altered");

        // And the event is consumed, not left behind to be delivered twice.
        let mut second = NovaSshEvent::default();
        assert_eq!(
            NOVA_SSH_RESULT_OK,
            nova_ssh_poll_event(session, &mut second, buffer.as_mut_ptr(), buffer.len())
        );

        assert_eq!(NOVA_SSH_RESULT_OK, nova_ssh_close(session));
    }

    #[test]
    fn exactly_sized_buffer_is_accepted() {
        // Boundary between TooSmall and Ready. An off-by-one here would either reject a correctly
        // sized buffer forever (livelock in the managed retry loop) or overrun it.
        let payload = b"0123456789";
        let session = create_test_session_with_event(NovaSshEventKind::HostKeyPrompt, payload);

        let mut event = NovaSshEvent::default();
        let mut buffer = [0u8; 10];
        assert_eq!(
            NOVA_SSH_RESULT_EVENT_READY,
            nova_ssh_poll_event(session, &mut event, buffer.as_mut_ptr(), buffer.len())
        );
        assert_eq!(payload.as_slice(), &buffer[..]);

        assert_eq!(NOVA_SSH_RESULT_OK, nova_ssh_close(session));
    }

    // Bug in the pre-#173 code, fixed as a side effect and worth pinning: a caller passing a null
    // payload pointer with a large capacity passed the size check, skipped the copy (guarded on
    // !payload.is_null()) and then *popped the event anyway* - silently destroying it. Now a null
    // pointer is treated as zero capacity, so the event survives for a real retry.
    #[test]
    fn null_payload_pointer_does_not_consume_the_event() {
        let payload = b"payload that must survive a header-only poll";
        let session = create_test_session_with_event(NovaSshEventKind::HostKeyPrompt, payload);

        let mut event = NovaSshEvent::default();
        assert_eq!(
            NOVA_SSH_RESULT_BUFFER_TOO_SMALL,
            nova_ssh_poll_event(session, &mut event, ptr::null_mut(), 4096)
        );
        assert_eq!(payload.len() as u32, event.payload_len);

        let mut buffer = vec![0u8; event.payload_len as usize];
        assert_eq!(
            NOVA_SSH_RESULT_EVENT_READY,
            nova_ssh_poll_event(session, &mut event, buffer.as_mut_ptr(), buffer.len())
        );
        assert_eq!(payload.as_slice(), &buffer[..]);

        assert_eq!(NOVA_SSH_RESULT_OK, nova_ssh_close(session));
    }

    #[test]
    fn queued_events_are_delivered_in_order() {
        // take_event_if_fits pops under the same lock it peeked under; this guards the ordering that
        // guarantees.
        let shared = Arc::new(SharedState::new());
        for i in 0u8..5 {
            shared.queue_event(QueuedEvent {
                kind: NovaSshEventKind::HostKeyPrompt,
                payload: vec![i; 3],
                status_code: i as i32,
                flags: NOVA_SSH_EVENT_FLAG_JSON,
            });
        }
        let session = registry_insert(NovaSshSession {
            shared,
            command_tx: Mutex::new(None),
            worker: Mutex::new(None),
        }) as usize;

        for i in 0u8..5 {
            let mut event = NovaSshEvent::default();
            let mut buffer = [0u8; 8];
            assert_eq!(
                NOVA_SSH_RESULT_EVENT_READY,
                nova_ssh_poll_event(session, &mut event, buffer.as_mut_ptr(), buffer.len())
            );
            assert_eq!(i as i32, event.status_code, "events came back out of order");
            assert_eq!([i, i, i], buffer[..3]);
        }

        assert_eq!(NOVA_SSH_RESULT_OK, nova_ssh_close(session));
    }

    #[test]
    fn poll_copies_payload_when_buffer_is_large_enough() {
        let payload = b"hello from ssh";
        let session = create_test_session_with_event(NovaSshEventKind::Data, payload);
        assert_ne!(0, session);

        let mut event = NovaSshEvent::default();
        let mut buffer = [0u8; 64];
        let rc = nova_ssh_poll_event(session, &mut event, buffer.as_mut_ptr(), buffer.len());

        assert_eq!(NOVA_SSH_RESULT_EVENT_READY, rc);
        assert_eq!(NovaSshEventKind::Data as u32, event.kind);
        assert_eq!(payload.len() as u32, event.payload_len);
        assert_eq!(&buffer[..payload.len()], payload);

        let close = nova_ssh_close(session);
        assert_eq!(NOVA_SSH_RESULT_OK, close);
    }

    #[test]
    fn submit_response_queues_prompt_data_even_before_worker_loop_runs() {
        let shared = Arc::new(SharedState::new());
        let (command_tx, _command_rx) = mpsc::unbounded_channel();
        let session = registry_insert(NovaSshSession {
            shared: shared.clone(),
            command_tx: Mutex::new(Some(command_tx)),
            worker: Mutex::new(None),
        }) as usize;

        let payload = br#"{"accept":true}"#;
        let rc = nova_ssh_submit_response(
            session,
            NovaSshResponseKind::HostKeyDecision as u32,
            payload.as_ptr(),
            payload.len(),
        );

        assert_eq!(NOVA_SSH_RESULT_OK, rc);
        let queued = shared.wait_for_response(NovaSshResponseKind::HostKeyDecision);
        assert_eq!(Some(payload.to_vec()), queued);

        let close = nova_ssh_close(session);
        assert_eq!(NOVA_SSH_RESULT_OK, close);
    }

    #[test]
    fn connect_config_reads_keepalive_settings_from_ffi_args() {
        let host = CString::new("native.example").unwrap();
        let user = CString::new("nova").unwrap();
        let term = CString::new("xterm-256color").unwrap();

        let args = NovaSshConnectArgs {
            host: host.as_ptr(),
            user: user.as_ptr(),
            port: 22,
            cols: 120,
            rows: 30,
            term: term.as_ptr(),
            identity_file: ptr::null(),
            jump_host: ptr::null(),
            jump_user: ptr::null(),
            jump_port: 0,
            keepalive_interval_seconds: 15,
            keepalive_count_max: 7,
            remote_shell_kind: 0,
            shell_detection_command: ptr::null(),
            bash_cwd_bootstrap: ptr::null(),
            zsh_cwd_bootstrap: ptr::null(),
            fish_cwd_bootstrap: ptr::null(),
        };

        let config = ConnectConfig::from_args(&args).expect("config should parse");

        assert_eq!(15, config.keepalive_interval_seconds);
        assert_eq!(7, config.keepalive_count_max);
    }

    #[test]
    fn connect_config_reads_remote_shell_fields_from_ffi_args() {
        let host = CString::new("native.example").unwrap();
        let user = CString::new("nova").unwrap();
        let term = CString::new("xterm-256color").unwrap();
        let shell_detection_command = CString::new("sh -lc 'printf test'").unwrap();
        let bash_cwd_bootstrap = CString::new("bash-bootstrap").unwrap();
        let zsh_cwd_bootstrap = CString::new("zsh-bootstrap").unwrap();
        let fish_cwd_bootstrap = CString::new("fish-bootstrap").unwrap();

        let args = NovaSshConnectArgs {
            host: host.as_ptr(),
            user: user.as_ptr(),
            port: 22,
            cols: 120,
            rows: 30,
            term: term.as_ptr(),
            identity_file: ptr::null(),
            jump_host: ptr::null(),
            jump_user: ptr::null(),
            jump_port: 0,
            keepalive_interval_seconds: 15,
            keepalive_count_max: 7,
            remote_shell_kind: 2,
            shell_detection_command: shell_detection_command.as_ptr(),
            bash_cwd_bootstrap: bash_cwd_bootstrap.as_ptr(),
            zsh_cwd_bootstrap: zsh_cwd_bootstrap.as_ptr(),
            fish_cwd_bootstrap: fish_cwd_bootstrap.as_ptr(),
        };

        let config = ConnectConfig::from_args(&args).expect("config should parse");

        assert_eq!(RemoteShellKind::Zsh, config.remote_shell_kind);
        assert_eq!(
            Some("sh -lc 'printf test'".to_owned()),
            config.shell_detection_command
        );
        assert_eq!(Some("bash-bootstrap".to_owned()), config.bash_cwd_bootstrap);
        assert_eq!(Some("zsh-bootstrap".to_owned()), config.zsh_cwd_bootstrap);
        assert_eq!(Some("fish-bootstrap".to_owned()), config.fish_cwd_bootstrap);
    }

    #[test]
    fn detect_login_shell_output_to_kind_maps_known_tokens() {
        assert_eq!(
            RemoteShellKind::Bash,
            detect_login_shell_output_to_kind("/bin/bash")
        );
        assert_eq!(
            RemoteShellKind::Zsh,
            detect_login_shell_output_to_kind("zsh")
        );
        assert_eq!(
            RemoteShellKind::Fish,
            detect_login_shell_output_to_kind("/usr/local/bin/fish")
        );
        assert_eq!(
            RemoteShellKind::Pwsh,
            detect_login_shell_output_to_kind("powershell")
        );
        assert_eq!(
            RemoteShellKind::Auto,
            detect_login_shell_output_to_kind("tcsh")
        );
    }

    #[test]
    fn client_config_uses_keepalive_without_forcing_inactivity_timeout() {
        let config = ConnectConfig {
            host: "native.example".to_owned(),
            user: "nova".to_owned(),
            port: 22,
            cols: 120,
            rows: 30,
            term: "xterm-256color".to_owned(),
            identity_file: None,
            jump_host: None,
            keepalive_interval_seconds: 15,
            keepalive_count_max: 7,
            remote_shell_kind: RemoteShellKind::Auto,
            shell_detection_command: None,
            bash_cwd_bootstrap: None,
            zsh_cwd_bootstrap: None,
            fish_cwd_bootstrap: None,
        };

        let client_config = build_client_config(&config);

        assert_eq!(None, client_config.inactivity_timeout);
        assert_eq!(
            Some(Duration::from_secs(15)),
            client_config.keepalive_interval
        );
        assert_eq!(7, client_config.keepalive_max);
    }

    #[test]
    fn build_startup_command_wraps_bash_bootstrap() {
        let config = ConnectConfig {
            host: "native.example".to_owned(),
            user: "nova".to_owned(),
            port: 22,
            cols: 120,
            rows: 30,
            term: "xterm-256color".to_owned(),
            identity_file: None,
            jump_host: None,
            keepalive_interval_seconds: 30,
            keepalive_count_max: 3,
            remote_shell_kind: RemoteShellKind::Bash,
            shell_detection_command: None,
            bash_cwd_bootstrap: Some("printf 'cwd'".to_owned()),
            zsh_cwd_bootstrap: None,
            fish_cwd_bootstrap: None,
        };

        let command = build_startup_command(RemoteShellKind::Bash, &config)
            .expect("bash command should be generated");

        assert!(command.starts_with("sh -lc '"));
        assert!(command.contains("tmp_rc=$(mktemp)"));
        assert!(command.contains("exec bash --rcfile"));
        assert!(command.contains("~/.bash_profile"));
        assert!(command.contains("~/.bash_login"));
        assert!(command.contains("~/.profile"));
        assert!(command.contains("~/.bashrc"));
    }

    #[test]
    fn build_startup_command_wraps_fish_bootstrap_in_posix_shell() {
        let config = ConnectConfig {
            host: "native.example".to_owned(),
            user: "nova".to_owned(),
            port: 22,
            cols: 120,
            rows: 30,
            term: "xterm-256color".to_owned(),
            identity_file: None,
            jump_host: None,
            keepalive_interval_seconds: 30,
            keepalive_count_max: 3,
            remote_shell_kind: RemoteShellKind::Fish,
            shell_detection_command: None,
            bash_cwd_bootstrap: None,
            zsh_cwd_bootstrap: None,
            fish_cwd_bootstrap: Some("printf 'cwd'".to_owned()),
        };

        let command = build_startup_command(RemoteShellKind::Fish, &config)
            .expect("fish command should be generated");

        assert!(command.starts_with("sh -lc '"));
        assert!(command.contains("exec fish -i"));
        assert!(command.contains("XDG_CONFIG_HOME"));
    }

    #[test]
    fn build_startup_command_wraps_zsh_bootstrap_with_login_startup() {
        let config = ConnectConfig {
            host: "native.example".to_owned(),
            user: "nova".to_owned(),
            port: 22,
            cols: 120,
            rows: 30,
            term: "xterm-256color".to_owned(),
            identity_file: None,
            jump_host: None,
            keepalive_interval_seconds: 30,
            keepalive_count_max: 3,
            remote_shell_kind: RemoteShellKind::Zsh,
            shell_detection_command: None,
            bash_cwd_bootstrap: None,
            zsh_cwd_bootstrap: Some("print cwd".to_owned()),
            fish_cwd_bootstrap: None,
        };

        let command = build_startup_command(RemoteShellKind::Zsh, &config)
            .expect("zsh command should be generated");

        assert!(command.starts_with("sh -lc '"));
        assert!(command.contains("$tmp_dir/.zprofile"));
        assert!(command.contains("~/.zprofile"));
        assert!(command.contains("exec zsh -il"));
        assert!(command.contains("$tmp_dir/.zshrc"));
    }

    #[test]
    fn build_startup_command_returns_none_for_auto_or_pwsh() {
        let config = ConnectConfig {
            host: "native.example".to_owned(),
            user: "nova".to_owned(),
            port: 22,
            cols: 120,
            rows: 30,
            term: "xterm-256color".to_owned(),
            identity_file: None,
            jump_host: None,
            keepalive_interval_seconds: 30,
            keepalive_count_max: 3,
            remote_shell_kind: RemoteShellKind::Auto,
            shell_detection_command: Some("sh -lc 'printf bash'".to_owned()),
            bash_cwd_bootstrap: Some("printf 'cwd'".to_owned()),
            zsh_cwd_bootstrap: Some("print cwd".to_owned()),
            fish_cwd_bootstrap: Some("printf cwd".to_owned()),
        };

        assert_eq!(None, build_startup_command(RemoteShellKind::Auto, &config));
        assert_eq!(None, build_startup_command(RemoteShellKind::Pwsh, &config));
    }

    #[test]
    fn append_bounded_shell_detection_output_truncates_at_limit() {
        let mut output = vec![b'x'; SHELL_DETECTION_MAX_OUTPUT_BYTES - 2];

        let reached_limit = append_bounded_shell_detection_output(&mut output, b"abcd");

        assert!(reached_limit);
        assert_eq!(SHELL_DETECTION_MAX_OUTPUT_BYTES, output.len());
        assert_eq!(&output[output.len() - 2..], b"ab");
    }

    #[test]
    fn worker_resize_burst_should_only_apply_latest_dimensions() {
        let runtime = Builder::new_current_thread().enable_all().build().unwrap();

        runtime.block_on(async {
            let (command_tx, mut command_rx) = mpsc::unbounded_channel();
            command_tx
                .send(WorkerCommand::Resize {
                    cols: 120,
                    rows: 30,
                })
                .unwrap();
            command_tx
                .send(WorkerCommand::Resize {
                    cols: 140,
                    rows: 40,
                })
                .unwrap();
            command_tx
                .send(WorkerCommand::Resize {
                    cols: 160,
                    rows: 50,
                })
                .unwrap();
            drop(command_tx);

            let mut pending_command = None;
            let first_command = next_worker_command(&mut pending_command, &mut command_rx)
                .await
                .expect("first resize command should be available");

            let (cols, rows, pending_resize_command) = match first_command {
                WorkerCommand::Resize { cols, rows } => {
                    coalesce_pending_resize_commands(&mut command_rx, cols, rows)
                }
                _ => panic!("expected first worker command to be resize"),
            };

            pending_command = pending_resize_command;

            assert_eq!((160, 50), (cols, rows));
            assert!(pending_command.is_none());
            assert!(command_rx.recv().await.is_none());
        });
    }

    #[test]
    fn worker_resize_burst_preserves_intervening_non_resize_command_order() {
        let runtime = Builder::new_current_thread().enable_all().build().unwrap();

        runtime.block_on(async {
            let (command_tx, mut command_rx) = mpsc::unbounded_channel();
            command_tx
                .send(WorkerCommand::Resize {
                    cols: 120,
                    rows: 30,
                })
                .unwrap();
            command_tx
                .send(WorkerCommand::Resize {
                    cols: 140,
                    rows: 40,
                })
                .unwrap();
            command_tx
                .send(WorkerCommand::Write(vec![1, 2, 3]))
                .unwrap();
            command_tx
                .send(WorkerCommand::Resize {
                    cols: 160,
                    rows: 50,
                })
                .unwrap();
            drop(command_tx);

            let mut pending_command = None;

            let first_command = next_worker_command(&mut pending_command, &mut command_rx)
                .await
                .expect("first resize command should be available");
            let (cols, rows, pending_resize_command) = match first_command {
                WorkerCommand::Resize { cols, rows } => {
                    coalesce_pending_resize_commands(&mut command_rx, cols, rows)
                }
                _ => panic!("expected first worker command to be resize"),
            };

            pending_command = pending_resize_command;
            assert_eq!((140, 40), (cols, rows));

            match next_worker_command(&mut pending_command, &mut command_rx)
                .await
                .expect("pending write command should be preserved")
            {
                WorkerCommand::Write(data) => assert_eq!(vec![1, 2, 3], data),
                _ => panic!("expected pending worker command to be write"),
            }

            let second_command = next_worker_command(&mut pending_command, &mut command_rx)
                .await
                .expect("second resize command should still be queued");
            let (cols, rows, pending_resize_command) = match second_command {
                WorkerCommand::Resize { cols, rows } => {
                    coalesce_pending_resize_commands(&mut command_rx, cols, rows)
                }
                _ => panic!("expected second worker command to be resize"),
            };

            pending_command = pending_resize_command;
            assert_eq!((160, 50), (cols, rows));
            assert!(pending_command.is_none());
            assert!(command_rx.recv().await.is_none());
        });
    }

    #[test]
    fn resolve_upload_file_destination_path_appends_local_name_for_existing_directory() {
        let resolved = resolve_upload_file_destination_path("upload.txt", "/tmp", None, true)
            .expect("directory target should resolve");

        assert_eq!("/tmp/upload.txt", resolved);
    }

    #[test]
    fn resolve_upload_file_destination_path_expands_home_directory_shortcut() {
        let resolved =
            resolve_upload_file_destination_path("upload.txt", "~", Some("/home/nova"), false)
                .expect("home directory shortcut should resolve");

        assert_eq!("/home/nova/upload.txt", resolved);
    }

    #[test]
    fn should_check_for_cancellation_only_after_interval_is_reached() {
        assert!(!should_check_for_cancellation(64 * 1024));
        assert!(!should_check_for_cancellation(
            CANCELLATION_CHECK_INTERVAL_BYTES - 1
        ));
        assert!(should_check_for_cancellation(
            CANCELLATION_CHECK_INTERVAL_BYTES
        ));
    }

    // ---- #121 item 1: credential retention ----

    fn connection_request_with_password(password: Option<&str>) -> SftpConnectionRequest {
        let password_json = match password {
            Some(value) => format!(r#","password":"{value}""#),
            None => String::new(),
        };
        serde_json::from_str(&format!(
            r#"{{"host":"example.com","user":"nova","port":22{password_json},"knownHostsFilePath":"known_hosts.json"}}"#
        ))
        .expect("connection request should deserialize")
    }

    #[test]
    fn take_from_moves_the_password_out_of_the_request() {
        let mut connection = connection_request_with_password(Some("s3cret"));

        let auth = TransferAuthConfig::take_from(&mut connection);

        assert_eq!(Some("s3cret"), auth.password.as_deref().map(String::as_str));
        assert!(
            connection.password.is_none(),
            "the request must not retain a second copy of the credential"
        );
    }

    #[test]
    fn take_from_handles_a_request_without_a_password() {
        let mut connection = connection_request_with_password(None);

        let auth = TransferAuthConfig::take_from(&mut connection);

        assert!(auth.password.is_none());
        assert!(connection.password.is_none());
    }

    #[test]
    fn take_from_is_idempotent() {
        // A second call must not resurrect the credential from the request.
        let mut connection = connection_request_with_password(Some("s3cret"));

        let first = TransferAuthConfig::take_from(&mut connection);
        let second = TransferAuthConfig::take_from(&mut connection);

        assert!(first.password.is_some());
        assert!(second.password.is_none());
    }

    #[test]
    fn classify_sftp_transfer_error_uses_structured_error_kind_when_available() {
        let error = NativeSftpTransferError::new(
            NativeSftpTransferErrorKind::RemotePathNotFound,
            "Remote path not found: /tmp/missing",
        );
        let anyhow_error = anyhow::Error::new(error);

        let (result, status, message) = classify_sftp_transfer_error(&anyhow_error);

        assert_eq!(NOVA_SSH_RESULT_CLOSED, result);
        assert_eq!("error", status);
        assert_eq!("Remote path not found: /tmp/missing", message);
    }

    // ---- #104: server-supplied entry names must not escape the download root ----

    fn assert_entry_name_rejected(file_name: &str) {
        let error = validate_remote_entry_name(file_name)
            .expect_err(&format!("{file_name:?} should be rejected"));
        let native = error
            .downcast_ref::<NativeSftpTransferError>()
            .expect("rejection should be a structured transfer error");
        assert_eq!(
            NativeSftpTransferErrorKind::InvalidArgument,
            native.kind,
            "{file_name:?} should map to invalid-argument"
        );
    }

    #[test]
    fn validate_remote_entry_name_accepts_ordinary_names() {
        for name in [
            "file.txt",
            "nested-dir",
            "with space.log",
            "dot.in.middle",
            "..prefixed",
            "trailing..",
            "...",
            "\u{1f600}-emoji",
        ] {
            validate_remote_entry_name(name)
                .unwrap_or_else(|error| panic!("{name:?} should be accepted: {error}"));
        }
    }

    #[test]
    fn validate_remote_entry_name_rejects_parent_and_current_directory() {
        assert_entry_name_rejected("..");
        assert_entry_name_rejected(".");
    }

    #[test]
    fn validate_remote_entry_name_rejects_separators_and_traversal() {
        assert_entry_name_rejected("../evil");
        assert_entry_name_rejected("../../evil");
        assert_entry_name_rejected("nested/child");
        assert_entry_name_rejected("..\\evil");
        assert_entry_name_rejected("nested\\child");
    }

    #[test]
    fn validate_remote_entry_name_rejects_absolute_and_drive_relative_paths() {
        // Path::join with any of these silently replaces the download root.
        assert_entry_name_rejected("/etc/cron.d/payload");
        assert_entry_name_rejected("/");
        assert_entry_name_rejected("C:\\Windows\\System32\\payload.dll");
        assert_entry_name_rejected("\\\\server\\share\\payload");
    }

    #[test]
    fn validate_remote_entry_name_rejects_empty_and_nul_names() {
        assert_entry_name_rejected("");
        assert_entry_name_rejected("payload\0.txt");
    }

    #[test]
    // The absolute join is the whole point of this test: it pins the `Path::join`
    // replacement behaviour that makes validate_remote_entry_name necessary. Clippy's
    // join_absolute_paths lint is exactly the bug being demonstrated.
    #[allow(clippy::join_absolute_paths)]
    fn path_join_with_absolute_entry_name_would_escape_the_root() {
        // Documents *why* validation is required rather than relying on join semantics.
        let root = Path::new("/home/nova/downloads");
        let escaped = root.join("/etc/cron.d/payload");

        assert_eq!(Path::new("/etc/cron.d/payload"), escaped);
        assert!(!escaped.starts_with(root));
        assert!(ensure_within_download_root(root, &escaped).is_err());
    }

    #[test]
    fn ensure_within_download_root_accepts_nested_children() {
        let root = Path::new("/home/nova/downloads");

        ensure_within_download_root(root, &root.join("a"))
            .expect("direct child should be accepted");
        ensure_within_download_root(root, &root.join("a").join("b").join("c.txt"))
            .expect("nested child should be accepted");
    }

    #[test]
    fn ensure_within_download_root_rejects_sibling_prefix_collision() {
        // "downloads-evil" shares a string prefix with "downloads" but is not inside it.
        let root = Path::new("/home/nova/downloads");

        assert!(ensure_within_download_root(root, Path::new("/home/nova/downloads-evil/x")).is_err());
        assert!(ensure_within_download_root(root, Path::new("/home/nova/other")).is_err());
    }

    #[test]
    fn remote_basename_rejects_paths_resolving_to_parent_directory() {
        for path in ["/srv/data/..", "/srv/data/../", "..", "  ..  "] {
            let error = remote_basename(path)
                .expect_err(&format!("{path:?} should not yield a basename"));
            let native = error
                .downcast_ref::<NativeSftpTransferError>()
                .expect("rejection should be a structured transfer error");
            assert_eq!(NativeSftpTransferErrorKind::InvalidArgument, native.kind);
        }
    }

    #[test]
    fn remote_basename_still_resolves_ordinary_directories() {
        assert_eq!("data", remote_basename("/srv/data").expect("should resolve"));
        assert_eq!(
            "data",
            remote_basename("/srv/data/").expect("trailing slash should resolve")
        );
    }

    // ---- #144: partial downloads must not clobber the destination ----

    #[test]
    fn partial_download_path_appends_suffix_beside_the_destination() {
        let destination = Path::new("/home/nova/downloads/archive.tar.gz");
        let partial = partial_download_path(destination);
        let partial_name = partial
            .file_name()
            .and_then(|value| value.to_str())
            .expect("partial should have a name");

        // Same directory, so the rename is a cheap intra-volume move rather than a copy.
        assert_eq!(
            Path::new("/home/nova/downloads"),
            partial.parent().expect("partial should stay in place")
        );
        // Prefixed with the full destination name, so a stray file is traceable to it.
        // This also pins that with_extension is not used - that would turn
        // "archive.tar.gz" into "archive.tar.novapart" and rename to the wrong path.
        assert!(
            partial_name.starts_with("archive.tar.gz."),
            "unexpected partial name: {partial_name}"
        );
        assert!(
            partial_name.ends_with(PARTIAL_DOWNLOAD_SUFFIX),
            "unexpected partial name: {partial_name}"
        );
        assert_ne!(destination, partial);
    }

    #[test]
    fn partial_download_path_is_unique_per_call() {
        // Two concurrent downloads to the same destination must not share a scratch
        // file; a deterministic name let their writes interleave.
        let destination = Path::new("/home/nova/downloads/archive.tar.gz");
        let first = partial_download_path(destination);
        let second = partial_download_path(destination);

        assert_ne!(first, second);
    }

    #[test]
    fn create_partial_download_file_refuses_an_existing_path() {
        // O_EXCL is what stops a planted symlink at the scratch path from redirecting
        // the downloaded bytes: an existing path fails rather than being followed.
        let runtime = Builder::new_current_thread()
            .enable_all()
            .build()
            .expect("runtime should build");

        runtime.block_on(async {
            let dir = std::env::temp_dir().join(format!(
                "nova-exclusive-{}-{}",
                std::process::id(),
                PARTIAL_DOWNLOAD_COUNTER.fetch_add(1, Ordering::Relaxed)
            ));
            tokio::fs::create_dir_all(&dir)
                .await
                .expect("temp dir should be creatable");

            let occupied = dir.join("already-there.novapart");
            tokio::fs::write(&occupied, b"pre-existing content")
                .await
                .expect("pre-existing file should be writable");

            let error = create_partial_download_file(&occupied)
                .await
                .expect_err("existing path should be refused");
            assert!(
                !error.to_string().is_empty(),
                "refusal should carry a message"
            );

            // The pre-existing content must be untouched - not truncated.
            let preserved = tokio::fs::read(&occupied)
                .await
                .expect("pre-existing file should still be readable");
            assert_eq!(b"pre-existing content".to_vec(), preserved);

            // A fresh path in the same directory still succeeds.
            let fresh = dir.join("fresh.novapart");
            let file = create_partial_download_file(&fresh)
                .await
                .expect("fresh path should be creatable");
            drop(file);
            assert!(fresh.exists());

            let _ = tokio::fs::remove_dir_all(&dir).await;
        });
    }

    #[test]
    fn rename_replaces_an_existing_destination() {
        // download_file_from_remote commits by renaming the `.novapart` scratch file
        // over `local_path`, which assumes rename REPLACES an existing destination
        // rather than failing. That holds on all three target platforms today
        // (on Windows via SetFileInformationByHandle + FileRenameInfo.ReplaceIfExists),
        // but it is a platform guarantee this crate silently depends on rather than one
        // it controls: if it ever stopped holding, every re-download over an existing
        // file would fail and directory re-downloads would abort at the first such file.
        // Raised as a concern on PR #210; pinned here so a regression is caught by
        // `cargo test` instead of by users.
        let runtime = Builder::new_current_thread()
            .enable_all()
            .build()
            .expect("runtime should build");

        runtime.block_on(async {
            let dir = std::env::temp_dir().join(format!(
                "nova-rename-{}-{}",
                std::process::id(),
                PARTIAL_DOWNLOAD_COUNTER.fetch_add(1, Ordering::Relaxed)
            ));
            tokio::fs::create_dir_all(&dir)
                .await
                .expect("temp dir should be creatable");

            let destination = dir.join("archive.tar.gz");
            let partial = partial_download_path(&destination);
            tokio::fs::write(&destination, b"stale previous download")
                .await
                .expect("destination should be writable");
            tokio::fs::write(&partial, b"freshly downloaded bytes")
                .await
                .expect("partial should be writable");

            tokio::fs::rename(&partial, &destination)
                .await
                .expect("rename must replace an existing destination");

            assert_eq!(
                b"freshly downloaded bytes".to_vec(),
                tokio::fs::read(&destination)
                    .await
                    .expect("destination should be readable"),
                "destination should hold the newly downloaded bytes"
            );
            assert!(
                !partial.exists(),
                "the scratch file should be consumed by the rename"
            );

            let _ = tokio::fs::remove_dir_all(&dir).await;
        });
    }

    #[test]
    fn discard_partial_download_removes_the_file_and_tolerates_a_missing_one() {
        let runtime = Builder::new_current_thread()
            .enable_all()
            .build()
            .expect("runtime should build");

        runtime.block_on(async {
            let dir = std::env::temp_dir().join(format!(
                "nova-partial-{}-{:?}",
                std::process::id(),
                std::thread::current().id()
            ));
            tokio::fs::create_dir_all(&dir)
                .await
                .expect("temp dir should be creatable");
            let partial = dir.join("download.novapart");
            tokio::fs::write(&partial, b"partial bytes")
                .await
                .expect("partial file should be writable");
            assert!(partial.exists());

            discard_partial_download(&partial).await;
            assert!(!partial.exists(), "partial file should be removed");

            // Second call must not panic or error - cleanup runs on paths that may
            // already be gone.
            discard_partial_download(&partial).await;

            let _ = tokio::fs::remove_dir_all(&dir).await;
        });
    }

}

fn build_client_config(config: &ConnectConfig) -> client::Config {
    client::Config {
        inactivity_timeout: None,
        keepalive_interval: Some(Duration::from_secs(
            config.keepalive_interval_seconds as u64,
        )),
        keepalive_max: config.keepalive_count_max as usize,
        ..<_>::default()
    }
}

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

    #[test]
    fn poll_event_rejects_null_without_panic() {
        let rc = nova_ssh_poll_event(0, std::ptr::null_mut(), std::ptr::null_mut(), 0);
        assert_eq!(rc, NOVA_SSH_RESULT_INVALID_ARGUMENT);
    }
}

#[cfg(test)]
fn stub_session() -> NovaSshSession {
    NovaSshSession {
        shared: Arc::new(SharedState::new()),
        command_tx: Mutex::new(None),
        worker: Mutex::new(None),
    }
}

#[cfg(test)]
mod handle_abuse_tests {
    use super::*;

    #[test]
    fn calls_after_close_fail_closed() {
        let handle = registry_insert(stub_session()) as usize;
        assert_eq!(NOVA_SSH_RESULT_OK, nova_ssh_close(handle));

        let mut event = NovaSshEvent::default();
        assert_eq!(
            NOVA_SSH_RESULT_INVALID_ARGUMENT,
            nova_ssh_poll_event(handle, &mut event, std::ptr::null_mut(), 0)
        );
        assert_eq!(
            NOVA_SSH_RESULT_INVALID_ARGUMENT,
            nova_ssh_write(handle, [1u8].as_ptr(), 1)
        );
        assert_eq!(
            NOVA_SSH_RESULT_INVALID_ARGUMENT,
            nova_ssh_resize(handle, 80, 24)
        );
        assert_eq!(
            NOVA_SSH_RESULT_INVALID_ARGUMENT,
            nova_ssh_channel_eof(handle, 0)
        );
        assert_eq!(
            NOVA_SSH_RESULT_INVALID_ARGUMENT,
            nova_ssh_submit_response(handle, 2, br#"{}"#.as_ptr(), 2)
        );
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
                    assert!(matches!(
                        rc,
                        NOVA_SSH_RESULT_OK
                            | NOVA_SSH_RESULT_EVENT_READY
                            | NOVA_SSH_RESULT_INVALID_ARGUMENT
                    ));
                }
            });
            let closer = std::thread::spawn(move || nova_ssh_close(handle));
            poller.join().unwrap();
            let _ = closer.join().unwrap();
        }
    }

    #[test]
    fn two_concurrent_closes_yield_exactly_one_success() {
        // concurrent_poll_and_close_never_crashes races poll against a *single* closer.
        // This covers the other half of the #121 concern: two racing closers must not
        // both observe success, or a caller could conclude it owns a teardown twice.
        for _ in 0..200 {
            let handle = registry_insert(stub_session()) as usize;
            let barrier = Arc::new(std::sync::Barrier::new(2));

            let first_barrier = Arc::clone(&barrier);
            let first = std::thread::spawn(move || {
                first_barrier.wait();
                nova_ssh_close(handle)
            });
            let second_barrier = Arc::clone(&barrier);
            let second = std::thread::spawn(move || {
                second_barrier.wait();
                nova_ssh_close(handle)
            });

            let results = [
                first.join().expect("closer should not panic"),
                second.join().expect("closer should not panic"),
            ];

            assert_eq!(
                1,
                results
                    .iter()
                    .filter(|rc| **rc == NOVA_SSH_RESULT_OK)
                    .count(),
                "exactly one close must win, got {results:?}"
            );
            assert_eq!(
                1,
                results
                    .iter()
                    .filter(|rc| **rc == NOVA_SSH_RESULT_INVALID_ARGUMENT)
                    .count(),
                "the losing close must be refused, got {results:?}"
            );
        }
    }

    #[test]
    fn handle_ids_are_not_reused_after_close() {
        // Underpins calls_after_close_fail_closed: if the registry recycled ids, a stale
        // handle could silently address a *different* live session rather than being
        // rejected, and "fail closed" would quietly become "act on the wrong session".
        let first = registry_insert(stub_session()) as usize;
        assert_eq!(NOVA_SSH_RESULT_OK, nova_ssh_close(first));
        let second = registry_insert(stub_session()) as usize;

        assert_ne!(first, second, "a closed handle id must not be reissued");
        assert_eq!(NOVA_SSH_RESULT_OK, nova_ssh_close(second));
    }
}

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
