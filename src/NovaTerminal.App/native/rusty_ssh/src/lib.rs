use libc::{c_char, c_int};
use russh::client::{self, AuthResult, KeyboardInteractiveAuthResponse};
use russh::keys::{load_secret_key, ssh_key, PrivateKeyWithHashAlg};
use russh::{ChannelMsg, Disconnect};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, VecDeque};
use std::ffi::{CStr, CString};
use std::future::Future;
use std::io::Cursor;
use std::path::Path;
use std::ptr;
use std::sync::{Arc, Condvar, Mutex, mpsc as std_mpsc};
use std::thread;
use std::time::Duration;
use tokio::runtime::Builder;
use tokio::sync::mpsc;

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

const NOVA_SSH_EVENT_FLAG_JSON: u32 = 1;
const NOVA_SSH_EVENT_FLAG_BINARY: u32 = 2;

pub struct NovaSshSession {
    shared: Arc<SharedState>,
    command_tx: Option<mpsc::UnboundedSender<WorkerCommand>>,
    worker: Option<thread::JoinHandle<()>>,
}

struct SharedState {
    events: Mutex<VecDeque<QueuedEvent>>,
    responses: Mutex<VecDeque<QueuedResponse>>,
    response_cv: Condvar,
    closed: Mutex<bool>,
}

struct QueuedEvent {
    kind: NovaSshEventKind,
    payload: Vec<u8>,
    status_code: i32,
    flags: u32,
}

struct QueuedResponse {
    kind: NovaSshResponseKind,
    payload: Vec<u8>,
}

enum WorkerCommand {
    Write(Vec<u8>),
    Resize { cols: u16, rows: u16 },
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
    identity_file_path: Option<String>,
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
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SftpTransferResponse<'a> {
    status: &'a str,
    message: &'a str,
}

impl SharedState {
    fn new() -> Self {
        Self {
            events: Mutex::new(VecDeque::new()),
            responses: Mutex::new(VecDeque::new()),
            response_cv: Condvar::new(),
            closed: Mutex::new(false),
        }
    }

    fn queue_event(&self, event: QueuedEvent) {
        if *self.closed.lock().expect("closed mutex poisoned") {
            return;
        }

        self.events.lock().expect("events mutex poisoned").push_back(event);
    }

    fn peek_event(&self) -> Option<QueuedEvent> {
        self.events
            .lock()
            .expect("events mutex poisoned")
            .front()
            .map(|event| QueuedEvent {
                kind: event.kind,
                payload: event.payload.clone(),
                status_code: event.status_code,
                flags: event.flags,
            })
    }

    fn pop_event(&self) {
        let _ = self.events.lock().expect("events mutex poisoned").pop_front();
    }

    fn queue_response(&self, response: QueuedResponse) {
        self.responses
            .lock()
            .expect("responses mutex poisoned")
            .push_back(response);
        self.response_cv.notify_all();
    }

    fn wait_for_response(&self, kind: NovaSshResponseKind) -> Option<Vec<u8>> {
        let mut guard = self.responses.lock().expect("responses mutex poisoned");
        loop {
            if let Some(index) = guard.iter().position(|item| item.kind == kind) {
                return guard.remove(index).map(|item| item.payload);
            }

            if *self.closed.lock().expect("closed mutex poisoned") {
                return None;
            }

            guard = self
                .response_cv
                .wait(guard)
                .expect("responses mutex poisoned after wait");
        }
    }

    fn mark_closed(&self) {
        *self.closed.lock().expect("closed mutex poisoned") = true;
        self.response_cv.notify_all();
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

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_connect(args: *const NovaSshConnectArgs) -> *mut NovaSshSession {
    let config = match ConnectConfig::from_args(args) {
        Some(config) => config,
        None => return ptr::null_mut(),
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
            payload: serde_json::to_vec(&ClosedPayload { reason: "session-ended" })
                .unwrap_or_default(),
            status_code: 0,
            flags: NOVA_SSH_EVENT_FLAG_JSON,
        });
        worker_shared.mark_closed();
    });

    let session = NovaSshSession {
        shared,
        command_tx: Some(command_tx),
        worker: Some(worker),
    };

    Box::into_raw(Box::new(session))
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_poll_event(
    session: *mut NovaSshSession,
    event: *mut NovaSshEvent,
    payload: *mut u8,
    payload_capacity: usize,
) -> c_int {
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
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_write(
    session: *mut NovaSshSession,
    data: *const u8,
    data_len: usize,
) -> c_int {
    if session.is_null() || (data.is_null() && data_len != 0) {
        return NOVA_SSH_RESULT_INVALID_ARGUMENT;
    }

    let session = unsafe { &mut *session };
    let tx = match &session.command_tx {
        Some(sender) => sender,
        None => return NOVA_SSH_RESULT_CLOSED,
    };

    let bytes = if data_len == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(data, data_len) }.to_vec()
    };

    tx.send(WorkerCommand::Write(bytes))
        .map(|_| NOVA_SSH_RESULT_OK)
        .unwrap_or(NOVA_SSH_RESULT_CLOSED)
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_resize(
    session: *mut NovaSshSession,
    cols: u16,
    rows: u16,
) -> c_int {
    if session.is_null() || cols == 0 || rows == 0 {
        return NOVA_SSH_RESULT_INVALID_ARGUMENT;
    }

    let session = unsafe { &mut *session };
    let tx = match &session.command_tx {
        Some(sender) => sender,
        None => return NOVA_SSH_RESULT_CLOSED,
    };

    tx.send(WorkerCommand::Resize { cols, rows })
        .map(|_| NOVA_SSH_RESULT_OK)
        .unwrap_or(NOVA_SSH_RESULT_CLOSED)
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_open_direct_tcpip(
    session: *mut NovaSshSession,
    args: *const NovaSshDirectTcpIpArgs,
) -> c_int {
    if session.is_null() || args.is_null() {
        return NOVA_SSH_RESULT_INVALID_ARGUMENT;
    }

    let args = unsafe { args.as_ref() }.expect("validated non-null args");
    let host_to_connect = match read_c_string(args.host_to_connect) {
        Some(value) => value,
        None => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
    };
    let originator_address =
        read_c_string(args.originator_address).unwrap_or_else(|| "127.0.0.1".to_owned());

    let session = unsafe { &mut *session };
    let tx = match &session.command_tx {
        Some(sender) => sender,
        None => return NOVA_SSH_RESULT_CLOSED,
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

    if tx.send(command).is_err() {
        return NOVA_SSH_RESULT_CLOSED;
    }

    match reply_rx.recv() {
        Ok(Ok(channel_id)) => channel_id as c_int,
        Ok(Err(_)) => NOVA_SSH_RESULT_CHANNEL_OPEN_FAILED,
        Err(_) => NOVA_SSH_RESULT_CLOSED,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_channel_write(
    session: *mut NovaSshSession,
    channel_id: u32,
    data: *const u8,
    data_len: usize,
) -> c_int {
    if session.is_null() || (data.is_null() && data_len != 0) {
        return NOVA_SSH_RESULT_INVALID_ARGUMENT;
    }

    let session = unsafe { &mut *session };
    let tx = match &session.command_tx {
        Some(sender) => sender,
        None => return NOVA_SSH_RESULT_CLOSED,
    };

    let bytes = if data_len == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(data, data_len) }.to_vec()
    };

    tx.send(WorkerCommand::WriteForwardChannel {
        channel_id,
        data: bytes,
    })
    .map(|_| NOVA_SSH_RESULT_OK)
    .unwrap_or(NOVA_SSH_RESULT_CLOSED)
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_channel_eof(session: *mut NovaSshSession, channel_id: u32) -> c_int {
    if session.is_null() {
        return NOVA_SSH_RESULT_INVALID_ARGUMENT;
    }

    let session = unsafe { &mut *session };
    let tx = match &session.command_tx {
        Some(sender) => sender,
        None => return NOVA_SSH_RESULT_CLOSED,
    };

    tx.send(WorkerCommand::ForwardChannelEof { channel_id })
        .map(|_| NOVA_SSH_RESULT_OK)
        .unwrap_or(NOVA_SSH_RESULT_CLOSED)
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_channel_close(session: *mut NovaSshSession, channel_id: u32) -> c_int {
    if session.is_null() {
        return NOVA_SSH_RESULT_INVALID_ARGUMENT;
    }

    let session = unsafe { &mut *session };
    let tx = match &session.command_tx {
        Some(sender) => sender,
        None => return NOVA_SSH_RESULT_CLOSED,
    };

    tx.send(WorkerCommand::CloseForwardChannel { channel_id })
        .map(|_| NOVA_SSH_RESULT_OK)
        .unwrap_or(NOVA_SSH_RESULT_CLOSED)
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_submit_response(
    session: *mut NovaSshSession,
    response_kind: u32,
    data: *const u8,
    data_len: usize,
) -> c_int {
    if session.is_null() || (data.is_null() && data_len != 0) {
        return NOVA_SSH_RESULT_INVALID_ARGUMENT;
    }

    let kind = match response_kind {
        1 => NovaSshResponseKind::HostKeyDecision,
        2 => NovaSshResponseKind::Password,
        3 => NovaSshResponseKind::Passphrase,
        4 => NovaSshResponseKind::KeyboardInteractive,
        _ => return NOVA_SSH_RESULT_INVALID_ARGUMENT,
    };

    let session = unsafe { &mut *session };
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
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_close(session: *mut NovaSshSession) -> c_int {
    if session.is_null() {
        return NOVA_SSH_RESULT_INVALID_ARGUMENT;
    }

    let mut session = unsafe { Box::from_raw(session) };

    if let Some(tx) = session.command_tx.take() {
        let _ = tx.send(WorkerCommand::Close);
    }

    session.shared.mark_closed();

    if let Some(worker) = session.worker.take() {
        let _ = worker.join();
    }

    NOVA_SSH_RESULT_OK
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_sftp_transfer(
    request_json: *const c_char,
    response_json: *mut *mut c_char,
) -> c_int {
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

    write_sftp_response_json(
        response_json,
        NOVA_SSH_RESULT_NOT_IMPLEMENTED,
        "not-implemented",
        "Native backend stub reached: SFTP transfer not implemented.",
    )
}

#[unsafe(no_mangle)]
pub extern "C" fn nova_ssh_string_free(value: *mut c_char) {
    if value.is_null() {
        return;
    }

    unsafe {
        drop(CString::from_raw(value));
    }
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
            port: if args.jump_port == 0 { 22 } else { args.jump_port },
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
        })
    }
}

fn read_c_string(value: *const c_char) -> Option<String> {
    if value.is_null() {
        return None;
    }

    let string = unsafe { CStr::from_ptr(value) }.to_string_lossy().trim().to_owned();
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

    match CString::new(json) {
        Ok(value) => unsafe {
            *response_json = value.into_raw();
            result
        },
        Err(_) => result,
    }
}

fn sftp_request_has_blank_fields(request: &SftpTransferRequest) -> bool {
    let jump_host_is_blank = request
        .connection
        .jump_host
        .as_ref()
        .is_some_and(|jump_host| {
            jump_host.host.trim().is_empty()
                || jump_host.user.as_deref().is_some_and(|user| user.trim().is_empty())
                || jump_host.port == 0
        });

    request.connection.host.trim().is_empty()
        || request.connection.user.trim().is_empty()
        || request.connection.port == 0
        || request
            .connection
            .identity_file_path
            .as_deref()
            .is_some_and(|path| path.trim().is_empty())
        || jump_host_is_blank
        || request.transfer.direction.trim().is_empty()
        || request.transfer.kind.trim().is_empty()
        || request.transfer.local_path.trim().is_empty()
        || request.transfer.remote_path.trim().is_empty()
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

        let jump_session = if let Some(jump_host) = &config.jump_host {
            let jump_handler = NovaClientHandler {
                shared: shared.clone(),
                host: jump_host.host.clone(),
                port: jump_host.port,
            };

            let mut jump = client::connect(
                client_config.clone(),
                (jump_host.host.as_str(), jump_host.port),
                jump_handler,
            )
            .await?;

            authenticate(
                &jump_host.user,
                config.identity_file.as_deref(),
                &shared,
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
            let stream = jump
                .channel_open_direct_tcpip(config.host.clone(), config.port as u32, "127.0.0.1", 0)
                .await?
                .into_stream();

            client::connect_stream(client_config.clone(), stream, handler).await?
        } else {
            client::connect(client_config.clone(), (config.host.as_str(), config.port), handler).await?
        };

        authenticate(
            &config.user,
            config.identity_file.as_deref(),
            &shared,
            &mut session,
        )
        .await?;

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
        channel.request_shell(true).await?;

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
            Ok(WorkerCommand::Resize { cols: next_cols, rows: next_rows }) => {
                cols = next_cols;
                rows = next_rows;
            }
            Ok(command) => {
                pending_command = Some(command);
                break;
            }
            Err(mpsc::error::TryRecvError::Empty) | Err(mpsc::error::TryRecvError::Disconnected) => {
                break;
            }
        }
    }

    (cols, rows, pending_command)
}

async fn open_direct_tcpip_channel(
    session: &client::Handle<NovaClientHandler>,
    forward_channels: Arc<tokio::sync::Mutex<HashMap<u32, Arc<russh::ChannelWriteHalf<client::Msg>>>>>,
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
    forward_channels: Arc<tokio::sync::Mutex<HashMap<u32, Arc<russh::ChannelWriteHalf<client::Msg>>>>>,
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
    forward_channels: Arc<tokio::sync::Mutex<HashMap<u32, Arc<russh::ChannelWriteHalf<client::Msg>>>>>,
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
    forward_channels: Arc<tokio::sync::Mutex<HashMap<u32, Arc<russh::ChannelWriteHalf<client::Msg>>>>>,
    channel_id: u32,
) -> anyhow::Result<()> {
    let writer = forward_channels.lock().await.remove(&channel_id);
    if let Some(writer) = writer {
        writer.close().await?;
    }

    Ok(())
}

async fn close_all_forward_channels(
    forward_channels: Arc<tokio::sync::Mutex<HashMap<u32, Arc<russh::ChannelWriteHalf<client::Msg>>>>>,
) {
    let writers = {
        let mut channels = forward_channels.lock().await;
        channels.drain().map(|(_, writer)| writer).collect::<Vec<_>>()
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
        if let Some(auth_result) = try_public_key_auth(user, shared, session, identity_file).await? {
            if auth_result.success() {
                return Ok(());
            }
        }
    }

    let password = prompt_text(shared, NovaSshEventKind::PasswordPrompt, "Password:", NovaSshResponseKind::Password)?;
    let password_auth = session
        .authenticate_password(user.to_owned(), password)
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
                response = session.authenticate_keyboard_interactive_respond(responses).await?;
            }
        }
    }
}

fn prompt_text(
    shared: &Arc<SharedState>,
    event_kind: NovaSshEventKind,
    prompt: &str,
    response_kind: NovaSshResponseKind,
) -> anyhow::Result<String> {
    shared.queue_event(QueuedEvent {
        kind: event_kind,
        payload: serde_json::to_vec(&TextPromptPayload { prompt })?,
        status_code: 0,
        flags: NOVA_SSH_EVENT_FLAG_JSON,
    });

    let payload = shared
        .wait_for_response(response_kind)
        .ok_or_else(|| anyhow::anyhow!("SSH prompt canceled"))?;
    let response = serde_json::from_slice::<TextResponse>(&payload)?;
    Ok(response.text)
}

fn wait_keyboard_responses(shared: &Arc<SharedState>) -> anyhow::Result<Vec<String>> {
    let payload = shared
        .wait_for_response(NovaSshResponseKind::KeyboardInteractive)
        .ok_or_else(|| anyhow::anyhow!("Keyboard-interactive prompt canceled"))?;
    let response = serde_json::from_slice::<KeyboardInteractiveResponse>(&payload)?;
    Ok(response.responses)
}

#[cfg(test)]
fn create_test_session_with_event(kind: NovaSshEventKind, payload: &[u8]) -> *mut NovaSshSession {
    let shared = Arc::new(SharedState::new());
    shared.queue_event(QueuedEvent {
        kind,
        payload: payload.to_vec(),
        status_code: 0,
        flags: NOVA_SSH_EVENT_FLAG_JSON,
    });

    Box::into_raw(Box::new(NovaSshSession {
        shared,
        command_tx: None,
        worker: None,
    }))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn null_handle_operations_return_invalid_argument() {
        let resize = nova_ssh_resize(ptr::null_mut(), 120, 30);
        let write = nova_ssh_write(ptr::null_mut(), [1u8, 2, 3].as_ptr(), 3);
        let forward_args = NovaSshDirectTcpIpArgs {
            host_to_connect: ptr::null(),
            port_to_connect: 80,
            originator_address: ptr::null(),
            originator_port: 1000,
        };
        let open = nova_ssh_open_direct_tcpip(ptr::null_mut(), &forward_args);
        let channel_write = nova_ssh_channel_write(ptr::null_mut(), 1, [1u8, 2, 3].as_ptr(), 3);
        let channel_eof = nova_ssh_channel_eof(ptr::null_mut(), 1);
        let channel_close = nova_ssh_channel_close(ptr::null_mut(), 1);
        let respond = nova_ssh_submit_response(ptr::null_mut(), 1, br#"{}"#.as_ptr(), 2);
        let mut sftp_response = ptr::null_mut();
        let sftp = nova_ssh_sftp_transfer(ptr::null(), &mut sftp_response);
        let close = nova_ssh_close(ptr::null_mut());

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
    fn sftp_transfer_stub_returns_not_implemented_response() {
        let request = CString::new(
            r#"{"connection":{"host":"example.com","user":"nova","port":22},"transfer":{"direction":"download","kind":"file","localPath":"local.txt","remotePath":"/tmp/remote.txt"}}"#,
        )
        .unwrap();
        let mut response = ptr::null_mut();

        let rc = nova_ssh_sftp_transfer(request.as_ptr(), &mut response);

        assert_eq!(NOVA_SSH_RESULT_NOT_IMPLEMENTED, rc);
        assert!(!response.is_null());

        let response_json = unsafe { CStr::from_ptr(response) }.to_str().unwrap();
        let payload: serde_json::Value = serde_json::from_str(response_json).unwrap();
        assert_eq!("not-implemented", payload["status"]);
        assert_eq!(
            "Native backend stub reached: SFTP transfer not implemented.",
            payload["message"]
        );

        nova_ssh_string_free(response);
    }

    #[test]
    fn poll_reports_required_payload_length_before_copying() {
        let payload = br#"{"host":"example.internal","fingerprint":"SHA256:test"}"#;
        let session = create_test_session_with_event(NovaSshEventKind::HostKeyPrompt, payload);
        assert!(!session.is_null());

        let mut event = NovaSshEvent::default();
        let mut tiny = [0u8; 8];
        let rc = nova_ssh_poll_event(session, &mut event, tiny.as_mut_ptr(), tiny.len());

        assert_eq!(NOVA_SSH_RESULT_BUFFER_TOO_SMALL, rc);
        assert_eq!(NovaSshEventKind::HostKeyPrompt as u32, event.kind);
        assert_eq!(payload.len() as u32, event.payload_len);

        let close = nova_ssh_close(session);
        assert_eq!(NOVA_SSH_RESULT_OK, close);
    }

    #[test]
    fn poll_copies_payload_when_buffer_is_large_enough() {
        let payload = b"hello from ssh";
        let session = create_test_session_with_event(NovaSshEventKind::Data, payload);
        assert!(!session.is_null());

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
        let session = Box::into_raw(Box::new(NovaSshSession {
            shared: shared.clone(),
            command_tx: Some(command_tx),
            worker: None,
        }));

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
        };

        let config = ConnectConfig::from_args(&args).expect("config should parse");

        assert_eq!(15, config.keepalive_interval_seconds);
        assert_eq!(7, config.keepalive_count_max);
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
        };

        let client_config = build_client_config(&config);

        assert_eq!(None, client_config.inactivity_timeout);
        assert_eq!(Some(Duration::from_secs(15)), client_config.keepalive_interval);
        assert_eq!(7, client_config.keepalive_max);
    }

    #[test]
    fn worker_resize_burst_should_only_apply_latest_dimensions() {
        let runtime = Builder::new_current_thread().enable_all().build().unwrap();

        runtime.block_on(async {
            let (command_tx, mut command_rx) = mpsc::unbounded_channel();
            command_tx
                .send(WorkerCommand::Resize { cols: 120, rows: 30 })
                .unwrap();
            command_tx
                .send(WorkerCommand::Resize { cols: 140, rows: 40 })
                .unwrap();
            command_tx
                .send(WorkerCommand::Resize { cols: 160, rows: 50 })
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
                .send(WorkerCommand::Resize { cols: 120, rows: 30 })
                .unwrap();
            command_tx
                .send(WorkerCommand::Resize { cols: 140, rows: 40 })
                .unwrap();
            command_tx
                .send(WorkerCommand::Write(vec![1, 2, 3]))
                .unwrap();
            command_tx
                .send(WorkerCommand::Resize { cols: 160, rows: 50 })
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
}

fn build_client_config(config: &ConnectConfig) -> client::Config {
    client::Config {
        inactivity_timeout: None,
        keepalive_interval: Some(Duration::from_secs(config.keepalive_interval_seconds as u64)),
        keepalive_max: config.keepalive_count_max as usize,
        ..<_>::default()
    }
}
