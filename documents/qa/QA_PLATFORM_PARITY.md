# QA: Platform Parity & Environment

**Objective**: Verify identical behavior across backends and operating systems.

## 1. Windows Environment
- **PWSH/CMD**: Verify cursor semantics, color interpretation, and environment variable propagation (`TERM=xterm-256color`).
- **WSL**: Ensure PTY communication is stable; verify that `Alt` keys are captured correctly for TUI tools like `mc` and `vim`.

## 2. Linux / Unix Parity
- **Behavior Verification**: Run `vttest` routines (Screen/Cursor/Colors).
- **Expected**: Precise match with the Windows version's interpretation.

## 3. Signals & Session Management
- **Action**: Send `Ctrl+C` / `Ctrl+D` / `Ctrl+\`.
- **Expected**: Signals propagate correctly to the underlying process (e.g., stopping a running build or exiting the shell).
- **Termination**: Closing the window/tab should send `SIGHUP` or equivalent to the child process.

## 4. SSH & Remote
- **Action**: SSH into a remote server.
- **Verification**: Ensure no desync or character corruption occurs during high latency or network jitter.
