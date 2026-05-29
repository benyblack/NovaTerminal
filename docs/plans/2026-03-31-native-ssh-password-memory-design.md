# Native SSH Password Memory Design

**Date:** 2026-03-31

## Goal

Add a native-SSH-only way to remember a password securely so repeat connections can authenticate without prompting every time.

## Scope

- Native SSH backend only
- Password prompts only
- Secure storage via existing `VaultService`
- Profile-level opt-in in the new SSH editor
- Runtime prompt support for saving a password after first successful entry

## Non-Goals

- No password storage in `SshProfile` JSON
- No OpenSSH password saving
- No private-key passphrase persistence in this change
- No keyboard-interactive answer persistence in this change
- No large refactor of SSH profile storage

## Existing Context

- The new SSH profile flow is built around `NovaTerminal.Core.Ssh.Models.SshProfile` and `NewSshConnectionViewModel`.
- `SshProfile` currently has no password field.
- `VaultService` already supports storing and resolving SSH passwords by profile id through `GetCanonicalSshProfileKey`, `GetSshPasswordForProfile`, and `SetSshPasswordForProfile`.
- The native SSH runtime prompt path goes through `SshInteractionService`.
- The legacy settings window still contains an old password textbox, but the current SSH manager/editor does not expose password storage.

## Recommended Approach

Use a native-only, vault-backed remember-password flow:

1. Add a native-only `Remember password in system vault` option in the new SSH connection editor.
2. Keep the password secret itself out of all profile JSON and runtime profile serialization.
3. Store and load the password through `VaultService`, keyed by the profile id.
4. On native password prompts, try the vault first and auto-answer if a saved password exists.
5. If no saved password exists, show the existing password dialog with an added `Remember password` checkbox for password prompts only.
6. If the user submits a password and checks remember, persist it immediately to the vault.

This keeps the profile model clean, uses the secure storage that already exists, and limits the feature to the native backend exactly as requested.

## UX

### SSH Editor

- Show a checkbox only when `BackendKind == Native`.
- Suggested label: `Remember password in system vault`
- This checkbox means:
  - if checked, the app may store a password for this profile in `VaultService`
  - if unchecked, any existing saved password for that profile should be removed

Important detail: the checkbox represents intent, not the password value itself. The profile editor does not need a persistent plaintext password textbox.

### Runtime Password Prompt

- Only native password prompts get save behavior.
- Add a checkbox to the auth prompt dialog when the prompt kind is `Password`.
- Suggested label: `Remember password`
- If a saved password already exists and the profile is configured to remember it, the dialog should not appear; the stored password should be submitted automatically.

Passphrase and keyboard-interactive prompts should keep the current transient behavior.

## Data Model

### Persisted Profile Data

Add a small boolean flag to `SshProfile`, for example:

- `RememberPasswordInVault`

This is safe to persist because it is only a preference flag and contains no secret material.

### Secret Storage

- Store the actual password using `VaultService.SetSshPasswordForProfile(...)`
- Read it using `VaultService.GetSshPasswordForProfile(...)`
- Use the existing canonical key format based on profile id

## Data Flow

### Save Flow

1. User edits a native SSH profile.
2. User checks or unchecks `Remember password in system vault`.
3. `SshConnectionService.SaveProfile(...)` persists the profile preference flag.
4. If the flag is turned off, any stored password for that profile is removed from the vault.

### Connect Flow

1. Native SSH session starts for a saved profile.
2. `SshInteractionService` receives a password prompt.
3. If the profile backend is native and `RememberPasswordInVault` is enabled:
   - resolve the runtime `TerminalProfile` / profile id
   - check `VaultService` for an existing password
4. If a saved password exists, return it immediately without showing a dialog.
5. Otherwise show the password dialog.
6. If the user checks `Remember password`, store the entered password after submission.

## Architecture Notes

- Keep all UI concerns in Avalonia view models and dialogs.
- Keep secure storage interactions in app-layer services, not terminal core.
- Do not change the VT/parser/rendering path.
- Do not add fallback behavior that silently leaks into OpenSSH.

## Risks

### Profile Context In Runtime Prompts

`SshInteractionService` currently receives an `SshInteractionRequest`, but the save/load decision also needs profile identity. The implementation should pass enough native-profile context into the interaction service without mixing UI into core session logic.

### Accidental OpenSSH Wiring

The editor checkbox and runtime storage logic must be explicitly gated on `SshBackendKind.Native`.

### Existing Saved Secrets

Turning off the remember option should remove any stored password for that profile so the UX stays predictable.

## Testing Strategy

- View model tests for native-only remember flag behavior
- Connection service tests for profile preference persistence
- Vault tests for canonical key usage and removal behavior
- Interaction service tests for:
  - auto-using a saved native password
  - showing a password prompt when none exists
  - saving a password when the prompt checkbox is selected
  - not applying remember behavior to passphrase or keyboard-interactive prompts

## Success Criteria

- Native SSH users can opt into saving passwords securely
- Saved passwords are reused automatically on later native password prompts
- Passwords are never written to profile JSON
- OpenSSH behavior remains unchanged
- Passphrase and keyboard-interactive prompts remain unchanged
