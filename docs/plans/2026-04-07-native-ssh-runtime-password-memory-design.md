# Native SSH Runtime Password Memory Design

**Date:** 2026-04-07

## Goal

Make native SSH password saving a runtime dialog decision instead of a profile-editor setting, so users can save a native SSH password the first time they are prompted without having to preconfigure the profile.

## Scope

- Native SSH backend only
- Password prompts only
- Secure storage via existing `VaultService`
- Runtime prompt support for saving a password after first successful entry
- Automatic reuse of saved passwords for later native password prompts

## Non-Goals

- No OpenSSH password saving
- No password storage in `SshProfile` JSON
- No private-key passphrase persistence in this change
- No keyboard-interactive answer persistence in this change
- No deletion of existing saved passwords when a dialog submit leaves remember unchecked

## Existing Context

- The current native SSH password-memory flow is gated by a persisted `RememberPasswordInVault` preference on `SshProfile`.
- The new SSH connection editor exposes a native-only `Remember password in system vault` checkbox.
- The runtime auth dialog only shows `Remember password` when the profile preference is already enabled.
- `VaultService` already supports storing and resolving SSH passwords by profile id through canonical SSH-profile keys.
- `SshInteractionService` already owns the UI-facing auth prompt flow and is the correct place to decide whether to reuse a saved password or show the password dialog.

## Problem

The current UX hides the save-password option behind a profile editor setting:

1. A native profile must first be edited.
2. The user must enable `Remember password in system vault`.
3. Only then does the runtime password dialog expose `Remember password`.

That makes the feature easy to miss and forces an unnecessary editor round-trip before the first successful login.

## Recommended Approach

Use a runtime-only, vault-backed remember-password flow for native SSH:

1. Remove the profile-level `Remember password in system vault` checkbox from the SSH editor.
2. Stop using `SshProfile.RememberPasswordInVault` as the gate for runtime dialog behavior.
3. For native password prompts, always try the vault first when profile identity is available.
4. If a saved password exists, return it immediately without showing the dialog.
5. If no saved password exists, show the existing password dialog with `Remember password` always available for native password prompts.
6. If the user checks `Remember password`, persist the entered password in `VaultService` for that profile.
7. If the user leaves `Remember password` unchecked, submit normally and leave any existing saved password untouched.

This keeps secrets out of profile JSON, removes a hidden prerequisite from the UX, and constrains password memory to the native backend exactly as requested.

## UX

### SSH Editor

- Remove the native-only `Remember password in system vault` checkbox.
- The editor should no longer surface password-memory configuration.
- Backend selection remains unchanged.

### Runtime Password Prompt

- Native password prompts always show `Remember password`.
- If a saved password already exists for the native profile, the dialog should not appear; the stored password should be submitted automatically.
- If the dialog is shown and the user checks `Remember password`, the password is stored after submission.
- If the dialog is shown and the user leaves `Remember password` unchecked, the password is not updated or removed in the vault.

Passphrase and keyboard-interactive prompts keep the current transient behavior.

## Data Model

### Persisted Profile Data

- `RememberPasswordInVault` should no longer control runtime behavior.
- For backward compatibility, the field can remain temporarily if removing it immediately would complicate profile migration or serialization, but it becomes ignored state.
- New code should not treat the field as the source of truth for whether password memory is available.

### Secret Storage

- Store the actual password using `VaultService.SetSshPasswordForProfile(...)`
- Read it using `VaultService.GetSshPasswordForProfile(...)`
- Continue using canonical profile-id keys

## Data Flow

### Connect Flow

1. Native SSH session starts for a saved profile.
2. `SshInteractionService` receives a password prompt.
3. If profile identity is available, query `VaultService` for an existing password for that profile.
4. If a saved password exists, return it immediately without showing the dialog.
5. Otherwise show the password dialog with `Remember password` enabled.
6. If the user checks `Remember password`, store the entered password after submission.
7. If the user does not check `Remember password`, return the entered password and do nothing to the vault.

### Existing Saved Passwords

- Existing saved native SSH passwords continue to work.
- Users do not need to re-edit profiles to keep benefiting from previously saved secrets.
- A normal dialog submit without remember must not erase an existing password.

## Architecture Notes

- Keep all UI concerns in Avalonia view models and dialogs.
- Keep secure-storage interactions in app-layer services, not terminal core.
- Do not change VT/parser/rendering paths.
- Do not widen this feature to OpenSSH.

## Risks

### Hidden Dependency From Old Profile Flag

If any runtime path still checks `RememberPasswordInVault`, the dialog can remain incorrectly hidden. The implementation must remove that gate everywhere relevant.

### Unexpected Secret Deletion

Unchecked dialog submit must not be treated as a signal to remove a saved password. This needs explicit tests.

### Backend Leakage

The runtime checkbox must remain native-password-only. Passphrase, keyboard-interactive, and OpenSSH paths must preserve current behavior.

## Testing Strategy

Add deterministic tests for:

- editor VM and/or view behavior proving the profile-level remember-password option is no longer surfaced
- native password prompts always exposing remember-password when the dialog is shown
- native password prompts auto-using an existing saved password without any persisted profile gate
- unchecked native password submissions leaving an existing saved password untouched
- passphrase and keyboard-interactive prompts remaining transient
