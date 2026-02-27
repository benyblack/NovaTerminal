# Research: Windows Terminal Sixel Implementation

**Goal**: Understand how Windows Terminal (WT) supports Sixel given that the system-default ConPTY filters it, and determine how Nova Terminal can achieve the same.

## 1. The "Secret" Architecture
Windows Terminal does **not** use the standard Windows Console Host (`conhost.exe`) found in `System32`. Instead, it bundles two custom binaries:
*   **`OpenConsole.exe`**: An open-source, evolved version of `conhost.exe` that includes newer features like Sixel rendering and properly passes through `DCS` sequences.
*   **`conpty.dll` (Redistributable)**: A custom build of the ConPTY library designed to communicate specifically with `OpenConsole.exe`.

## 2. Why Nova Terminal Faces Filtering
Nova Terminal currently links against the standard Windows API (`kernel32.dll` / system `conpty.dll`). 
*   **Result**: The OS launches the legacy `conhost.exe`.
*   **Limit**: This legacy host has a strict whitelist for escape sequences. It strips `DCS` (Sixel) and blocks `DA1` (Device Attributes) queries, even when the `PSEUDOCONSOLE_PASSTHROUGH` flag is set.

## 3. The Path to Native Sixel (Bypassing Tunneling)
To achieve "Native Sixel" support (where standard `img2sixel` works without tunneling), Nova Terminal would need to implement the **"App-Local ConPTY"** pattern:

1.  **Redistribute Binaries**: Bundle `OpenConsole.exe` and `conpty.dll` (approx. 10MB) with Nova Terminal.
2.  **Dynamic Loading**: Modify `native/src/lib.rs` to stop linking against system libraries for ConPTY calls.
3.  **Runtime Binding**: Manually load the bundled `conpty.dll` using `LoadLibrary` and use `GetProcAddress` to find `CreatePseudoConsole`.
4.  **Execution**: Calling this specific function will spawn the bundled `OpenConsole.exe` instead of the system host.

## 4. Recommendation
### Short Term: Stick to Tunneling (OSC 1339)
*   **Pros**: Zero deployment cost, no extra binaries, works immediately on all Windows 10/11 versions.
*   **Cons**: Requires tools to be aware of the tunnel (or use wrappers).
*   **Status**: **Implemented and Verified.**

### Long Term: Bundle OpenConsole
*   **Pros**: "It just works" for all standard Linux/Unix tools (lsix, neofetch, etc.) running in WSL/CMD.
*   **Cons**: Increases installer size, requires managing Microsoft binary redistribution, complex Rust FFI changes.
*   **Verdict**: Consider this for "Nova Terminal v2.0" or a dedicated "Pro" feature tier.
