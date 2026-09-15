
# NovaTerminal Native SSH – Codex Execution Pack (PR1–PR3)

This file contains ready-to-use prompts for Codex / Antigravity.

---

## PR1 — Backend Split Foundation

**Branch:** feat/ssh-native-backend-foundation

### Prompt
Build the architectural split for SSH backends in NovaTerminal without changing current behavior. Add SshBackendKind to SshProfile with default OpenSsh. Introduce SshSessionFactory, OpenSshSession, and a stub NativeSshSession. Introduce a lower-level IRemoteTerminalTransport abstraction for future native SSH, but do not wire any native implementation yet. Update TerminalPane and SessionManager to construct SSH sessions through the factory. Keep OpenSSH behavior exactly as it works today.

### Acceptance
- No behavior change
- Direct SSH still works
- Profiles load/save correctly

---

## PR2 — Native Rust SSH Spike

**Branch:** feat/ssh-native-rust-spike

### Prompt
Create a new Rust crate native/rusty_ssh as a spike for NovaTerminal native SSH. Use a Rust SSH library and implement only the minimum interactive terminal flow: connect, auth, host key eventing, PTY request, shell channel, read/write, resize, exit status, close. Expose a C ABI using poll-style functions rather than callbacks. Do not implement forwarding, jump hosts, or reconnect yet. Optimize for correctness and a narrow interop surface.

### Acceptance
- Can connect to SSH server
- Interactive shell works
- Resize works
- Exit status received

---

## PR3 — Native SSH Session Wrapper

**Branch:** feat/ssh-native-session-wrapper

### Prompt
Implement NativeSshSession in C# by wrapping the Rust native SSH spike behind the existing terminal session model. Use a background poll/event loop, decode bytes safely, and integrate with the current terminal output/input lifecycle. Route SSH backend selection through SshSessionFactory so profiles can choose Native or OpenSsh. Do not force native SSH into the RustPtySession model.

### Acceptance
- Native backend selectable
- Output renders in terminal
- Resize works
- Clean shutdown

---

## Notes
- Do NOT remove OpenSSH backend
- Use poll-based FFI
- Keep architecture clean and layered
