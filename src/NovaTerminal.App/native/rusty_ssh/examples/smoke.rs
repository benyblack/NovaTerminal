use serde_json::json;
use std::env;
use std::ffi::CString;
use std::thread;
use std::time::Duration;
use rusty_ssh::{
    nova_ssh_close, nova_ssh_connect, nova_ssh_poll_event, nova_ssh_submit_response,
    nova_ssh_write, NovaSshConnectArgs, NovaSshEvent, NovaSshEventKind, NovaSshResponseKind,
    NOVA_SSH_RESULT_BUFFER_TOO_SMALL, NOVA_SSH_RESULT_EVENT_READY, NOVA_SSH_RESULT_OK,
};

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.iter().any(|arg| arg == "--help" || arg == "-h") || args.len() < 3 {
        eprintln!(
            "Usage: smoke <host> <user> [port] [identity-file]\n\
             Env:\n\
             NOVA_SSH_PASSWORD       password response for password prompts\n\
             NOVA_SSH_PASSPHRASE     passphrase response for key prompts\n\
             NOVA_SSH_KBD_JSON       JSON array for keyboard-interactive responses\n\
             NOVA_SSH_AUTO_EXIT=1    send `exit` after shell connects"
        );
        return;
    }

    let host = CString::new(args[1].clone()).expect("host CString");
    let user = CString::new(args[2].clone()).expect("user CString");
    let port = args
        .get(3)
        .and_then(|value| value.parse::<u16>().ok())
        .unwrap_or(22);
    let term = CString::new("xterm-256color").expect("term CString");
    let identity = args
        .get(4)
        .map(|value| CString::new(value.clone()).expect("identity CString"));

    let connect_args = NovaSshConnectArgs {
        host: host.as_ptr(),
        user: user.as_ptr(),
        port,
        cols: 120,
        rows: 30,
        term: term.as_ptr(),
        identity_file: identity.as_ref().map_or(std::ptr::null(), |value| value.as_ptr()),
    };

    let session = nova_ssh_connect(&connect_args);
    if session.is_null() {
        eprintln!("Failed to create SSH session");
        std::process::exit(1);
    }

    let password = env::var("NOVA_SSH_PASSWORD").ok();
    let passphrase = env::var("NOVA_SSH_PASSPHRASE").ok();
    let keyboard = env::var("NOVA_SSH_KBD_JSON").ok();
    let auto_exit = env::var("NOVA_SSH_AUTO_EXIT").ok().as_deref() == Some("1");

    let mut event = NovaSshEvent::default();
    let mut buffer = vec![0u8; 4096];

    loop {
        let rc = nova_ssh_poll_event(session, &mut event, buffer.as_mut_ptr(), buffer.len());
        if rc == NOVA_SSH_RESULT_OK {
            thread::sleep(Duration::from_millis(25));
            continue;
        }

        if rc == NOVA_SSH_RESULT_BUFFER_TOO_SMALL {
            buffer.resize(event.payload_len as usize, 0);
            continue;
        }

        if rc != NOVA_SSH_RESULT_EVENT_READY {
            eprintln!("poll failed: {rc}");
            break;
        }

        let payload = &buffer[..event.payload_len as usize];
        match event.kind {
            kind if kind == NovaSshEventKind::HostKeyPrompt as u32 => {
                println!("host-key: {}", String::from_utf8_lossy(payload));
                let body = json!({ "accept": true }).to_string();
                let _ = nova_ssh_submit_response(
                    session,
                    NovaSshResponseKind::HostKeyDecision as u32,
                    body.as_ptr(),
                    body.len(),
                );
            }
            kind if kind == NovaSshEventKind::PasswordPrompt as u32 => {
                println!("password-prompt: {}", String::from_utf8_lossy(payload));
                let body = json!({ "text": password.clone().unwrap_or_default() }).to_string();
                let _ = nova_ssh_submit_response(
                    session,
                    NovaSshResponseKind::Password as u32,
                    body.as_ptr(),
                    body.len(),
                );
            }
            kind if kind == NovaSshEventKind::PassphrasePrompt as u32 => {
                println!("passphrase-prompt: {}", String::from_utf8_lossy(payload));
                let body = json!({ "text": passphrase.clone().unwrap_or_default() }).to_string();
                let _ = nova_ssh_submit_response(
                    session,
                    NovaSshResponseKind::Passphrase as u32,
                    body.as_ptr(),
                    body.len(),
                );
            }
            kind if kind == NovaSshEventKind::KeyboardInteractivePrompt as u32 => {
                println!("keyboard-interactive: {}", String::from_utf8_lossy(payload));
                let body = keyboard
                    .clone()
                    .unwrap_or_else(|| json!({ "responses": [] }).to_string());
                let _ = nova_ssh_submit_response(
                    session,
                    NovaSshResponseKind::KeyboardInteractive as u32,
                    body.as_ptr(),
                    body.len(),
                );
            }
            kind if kind == NovaSshEventKind::Data as u32 => {
                print!("{}", String::from_utf8_lossy(payload));
            }
            kind if kind == NovaSshEventKind::ExitStatus as u32 => {
                println!("exit-status: {}", String::from_utf8_lossy(payload));
            }
            kind if kind == NovaSshEventKind::Error as u32 => {
                eprintln!("error: {}", String::from_utf8_lossy(payload));
            }
            kind if kind == NovaSshEventKind::Connected as u32 => {
                println!("connected: {}", String::from_utf8_lossy(payload));
                if auto_exit {
                    let _ = nova_ssh_write(session, b"exit\n".as_ptr(), 5);
                }
            }
            kind if kind == NovaSshEventKind::Closed as u32 => {
                println!("closed: {}", String::from_utf8_lossy(payload));
                break;
            }
            _ => {}
        }
    }

    let _ = nova_ssh_close(session);
}
