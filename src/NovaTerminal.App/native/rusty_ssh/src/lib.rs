use libc::{c_char, c_int};
use russh::client::{self, AuthResult, KeyboardInteractiveAuthResponse};
use russh::keys::{load_secret_key, ssh_key, PrivateKeyWithHashAlg};
use russh::{ChannelMsg, Disconnect};
use serde::Serialize;
use std::collections::VecDeque;
use std::ffi::CStr;
use std::future::Future;
use std::path::Path;
use std::ptr;
use std::sync::{Arc, Condvar, Mutex};
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

impl ConnectConfig {
    fn from_args(args: *const NovaSshConnectArgs) -> Option<Self> {
        let args = unsafe { args.as_ref()? };
        let host = read_c_string(args.host)?;
        let user = read_c_string(args.user)?;
        let term = read_c_string(args.term).unwrap_or_else(|| "xterm-256color".to_owned());
        let identity_file = read_c_string(args.identity_file);

        Some(Self {
            host,
            user,
            port: if args.port == 0 { 22 } else { args.port },
            cols: if args.cols == 0 { 120 } else { args.cols },
            rows: if args.rows == 0 { 30 } else { args.rows },
            term,
            identity_file,
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

fn run_session(
    config: ConnectConfig,
    shared: Arc<SharedState>,
    mut command_rx: mpsc::UnboundedReceiver<WorkerCommand>,
) -> anyhow::Result<()> {
    let runtime = Builder::new_current_thread().enable_all().build()?;
    runtime.block_on(async move {
        let client_config = Arc::new(client::Config {
            inactivity_timeout: Some(Duration::from_secs(30)),
            ..<_>::default()
        });

        let handler = NovaClientHandler {
            shared: shared.clone(),
            host: config.host.clone(),
            port: config.port,
        };

        let mut session =
            client::connect(client_config, (config.host.as_str(), config.port), handler).await?;

        authenticate(&config, &shared, &mut session).await?;

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

        loop {
            tokio::select! {
                command = command_rx.recv() => {
                    match command {
                        Some(WorkerCommand::Write(data)) => {
                            channel.data(&data[..]).await?;
                        }
                        Some(WorkerCommand::Resize { cols, rows }) => {
                            channel.window_change(cols as u32, rows as u32, 0, 0).await?;
                        }
                        Some(WorkerCommand::Close) | None => {
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
        Ok(())
    })
}

async fn authenticate(
    config: &ConnectConfig,
    shared: &Arc<SharedState>,
    session: &mut client::Handle<NovaClientHandler>,
) -> anyhow::Result<()> {
    if let Some(identity_file) = &config.identity_file {
        if let Some(auth_result) = try_public_key_auth(config, shared, session, identity_file).await? {
            if auth_result.success() {
                return Ok(());
            }
        }
    }

    let password = prompt_text(shared, NovaSshEventKind::PasswordPrompt, "Password:", NovaSshResponseKind::Password)?;
    let password_auth = session
        .authenticate_password(config.user.clone(), password)
        .await?;
    if password_auth.success() {
        return Ok(());
    }

    let keyboard_auth = authenticate_keyboard_interactive(config, shared, session).await?;
    if keyboard_auth {
        return Ok(());
    }

    anyhow::bail!("SSH authentication failed")
}

async fn try_public_key_auth(
    config: &ConnectConfig,
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
            config.user.clone(),
            PrivateKeyWithHashAlg::new(Arc::new(key), hash_alg),
        )
        .await?;
    Ok(Some(auth))
}

async fn authenticate_keyboard_interactive(
    config: &ConnectConfig,
    shared: &Arc<SharedState>,
    session: &mut client::Handle<NovaClientHandler>,
) -> anyhow::Result<bool> {
    let mut response = session
        .authenticate_keyboard_interactive_start(config.user.clone(), None::<String>)
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
        let respond = nova_ssh_submit_response(ptr::null_mut(), 1, br#"{}"#.as_ptr(), 2);
        let close = nova_ssh_close(ptr::null_mut());

        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, resize);
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, write);
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, respond);
        assert_eq!(NOVA_SSH_RESULT_INVALID_ARGUMENT, close);
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
}
