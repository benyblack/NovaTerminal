# Vault key derivation security fix (#100)

**Status:** Approved design — ready for implementation plan
**Date:** 2026-06-11
**Issue:** #100 (P0, security) — *Vault key derivation on Linux/macOS is trivially decryptable*
**Area:** `src/NovaTerminal.App/Shell/VaultService.cs`

## Problem

On Linux/macOS, vault secrets (SSH credentials) are AES-encrypted with a key any
local process can derive:

- **Linux:** key = `PBKDF2(contents of /etc/machine-id, "NovaVaultSalt", 10_000)`.
  `/etc/machine-id` is world-readable → offline decryption by any local user.
- **macOS:** key = `PBKDF2($USER, same salt)` → effectively a public key.
- `catch { }` in `GetPlatformKey()` silently falls back to the constant string
  `"NovaTerminal-Fallback-Salt"` as key material.
- No vault-file permission enforcement.
- `MainWindow` constructs the vault as `try { Vault = new VaultService(); } catch { }`,
  so init failure silently disables the vault with no user signal.

The flaw is fundamental: encrypting at rest with a key derived from non-secret,
machine-readable data is decryptable by any local process. Fixing it requires a
*real* per-user secret. Windows already has this (DPAPI / Credential Manager keyed
to the Windows login) and is unaffected.

## Goals

1. Linux/macOS secrets are protected by a real per-user secret managed by the OS.
2. No code path derives an encryption key from non-secret machine data.
3. When secure storage is unavailable, the failure is surfaced to the user and the
   vault disables persistence — it never falls back to weak crypto.
4. Legacy weakly-encrypted vault files are removed, not migrated.
5. Vault construction and error state are consistent across the app.

## Non-goals

- Passphrase-based vault (rejected: competitor-parity UX requires no per-session
  prompt; the OS keychain provides that). May be layered on later.
- CLI shell-out to `secret-tool` / `security` (rejected: subprocess + D-Bus
  fragility, depends on optional tooling).
- Keychain key enumeration (`ListKeys()` is unused in production).
- `chmod 600` on a vault file — moot, because production no longer writes a secret
  file on Linux/macOS at all.

## Decisions (from brainstorming)

| Decision | Choice |
|---|---|
| Key source (Linux/macOS) | OS keychain via **P/Invoke** + no weak fallback |
| Linux backend | `libsecret-1.so.0` (Secret Service) |
| macOS backend | `Security.framework` Keychain Services |
| No keychain available | **Disable persistence + surface error** (no passphrase, no weak crypto) |
| Failure mode | Surface error; never silently fall back |
| Legacy `vault.dat` | **Abandon + delete without reading** |
| Construction | Fix silent-catch surfacing **+ single shared instance** |

## Architecture

### `ISecretStore`

`VaultService` today mixes policy (SSH key naming, profile resolution,
remember-password logic) with storage (DPAPI / Credential Manager / AES file).
Storage moves behind an interface:

```csharp
interface ISecretStore
{
    bool   IsAvailable { get; }        // can this store persist right now?
    string? Read(string key);
    void    Write(string key, string value);
    bool    Delete(string key);
}
```

`VaultService` retains all existing **policy** methods unchanged
(`GetCanonicalSshProfileKey`, `GetLegacySshKeys`, `ResolveSshPasswordForProfile`,
`ApplyRememberPasswordPreference`, `GetSshPasswordForProfile`,
`SetSshPasswordForProfile`, etc.) and delegates `GetSecret` / `SetSecret` /
`RemoveSecret` to an injected `ISecretStore`.

### Implementations

| Store | Platform | Backing |
|---|---|---|
| `WindowsCredentialStore` | Windows | existing `Win32CredentialManager` (logic unchanged, wrapped) |
| `MacKeychainStore` | macOS | `Security.framework`: `SecItemAdd` / `SecItemCopyMatching` / `SecItemUpdate` / `SecItemDelete` |
| `LinuxSecretStore` | Linux | `libsecret-1.so.0`: `secret_password_store_sync` / `secret_password_lookup_sync` / `secret_password_clear_sync` |
| `InMemorySecretStore` | tests | plain `Dictionary<string,string>` |

A factory `SecretStore.CreateDefault()` selects the platform store.
`VaultService()` (production) calls it; `VaultService(ISecretStore)` (tests/DI)
injects a store.

### Code removed

`GetPlatformKey()`, `EncryptFallback()`, `DecryptFallback()`, and the file-based
`Save()` / `TryLoadSecrets()` / `LoadSecretsOrEmpty()` crypto are **deleted**. The
weak-key path no longer exists anywhere in the codebase. The Windows credential
path is preserved (moved into `WindowsCredentialStore`).

## Disabled mode + failure surfacing

- Each keychain store probes availability at construction
  (libsecret: Secret Service reachable; macOS: keychain accessible). Probe failure
  → `IsAvailable == false`.
- When the store is unavailable, `VaultService.PersistenceAvailable == false`:
  - `GetSecret` returns `null`
  - `SetSecret` is a **no-op** (must **not** throw — SSH connect paths keep working)
  - `RemoveSecret` is a no-op
- `MainWindow` (currently `try { Vault = new VaultService(); } catch {}` at
  `MainWindow.axaml.cs:2354`): construct without swallowing; if
  `!Vault.PersistenceAvailable`, show a **one-time, non-blocking** notification
  ("Credential storage unavailable — SSH passwords won't be saved this session"),
  mirroring the existing recording-toast/notification pattern.

## Single shared instance

- One shared `VaultService` for the main app process, exposed via the existing
  `MainWindow.Vault` accessor. The scattered `new VaultService()` call sites resolve
  to it:
  - `Services/Ssh/RemoteDirectoryBrowserService.cs:37`
  - `Services/Ssh/RemotePathAutocompleteService.cs:33`
  - `Shell/SftpService.cs:596`
  - `Services/Ssh/SshInteractionService.cs:38` (default constructor param)
- **Exception — `SshAskPassCommand`** (`Shell/SshAskPassCommand.cs:45,253`) runs as a
  *separate helper process* and legitimately constructs its own `VaultService()`.
  Because secrets now live in the OS keychain (shared across processes), it reads
  exactly what the main app wrote — strictly more correct than the prior per-process
  in-memory dictionary.

## Legacy cleanup

- On startup, if `…/LocalAppData/NovaTerminal/vault.dat` exists on Linux/macOS,
  **delete it without reading** (best-effort; debug-log on failure). It was encrypted
  with the weak key and is a standing liability.
- Windows production never wrote `vault.dat` (it uses Credential Manager), so no
  Windows-side deletion is needed.

## P/Invoke specifics

- Use `LibraryImport` (source-generated; net10). Invoke the `dotnet-pinvoke` skill
  during implementation.
- **macOS:** CoreFoundation types (`CFStringRef` / `CFDataRef` / `CFDictionaryRef`)
  must be `CFRelease`d; handle `OSStatus` return codes
  (`errSecItemNotFound`, `errSecDuplicateItem` → update path).
- **Linux:** functions take `GError**`; on error free with `g_error_free`, and free
  returned heap strings with `secret_password_free`. Define a stable
  `SecretSchema` for NovaTerminal entries.
- Zero transient secret buffers in `finally` where practical; free all native
  allocations in `finally`.

## Testing

- **Migrate** the 14 `new VaultService(vaultPath)` test sites to
  `new VaultService(new InMemorySecretStore())`:
  - `tests/NovaTerminal.App.Tests/Core/VaultServiceSshKeyTests.cs` (~118–150) — where a
    test asserts cross-instance persistence, share one `InMemorySecretStore` between
    the two vaults.
  - `tests/NovaTerminal.App.Tests/Ssh/SshInteractionServiceTests.cs` (10 sites) — one
    store per test.
  - Removes disk I/O and temp-file cleanup from these tests.
- **New unit tests:**
  - `InMemorySecretStore` round-trip (write/read/delete).
  - Disabled-mode contract: unavailable store ⇒ `GetSecret` null, `SetSecret` /
    `RemoveSecret` no-op, `PersistenceAvailable == false`.
- **Keychain integration tests:** real round-trip against the live backend, marked
  **skippable** (skip when `IsAvailable == false`) so Linux CI without a D-Bus
  keyring does not fail — mirroring the existing category-quarantine convention.

## Scope & rollout

- Single PR on branch `fix/issue-100-vault-keychain-security` (off `main`).
- Run tests via the wrapper scripts (`scripts/build.ps1 test` / `scripts/build.sh`),
  targeted to the affected test project for fast feedback.
