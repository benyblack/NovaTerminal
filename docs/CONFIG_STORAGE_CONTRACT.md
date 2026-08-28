# Configuration storage contract

Where NovaTerminal keeps user state, what survives an update or an uninstall, and the
constraints any feature that reads or writes that state has to respect.

Written for anyone building backup/restore, export/import, profile sync, or a settings
migration. Every claim below cites its source so it can be re-checked rather than trusted.

## Two roots, deliberately different

| | Path | Owner | Deleted by uninstall? |
|---|---|---|---|
| **Config root** | `%LocalAppData%\NovaTerminal` | the app (`AppPaths`) | **No** |
| **Install root** | `%LocalAppData%\NovaTerminalApp` | Velopack (`--packId`) | **Yes — entirely** |

Velopack installs to `%LocalAppData%\<packId>` and its uninstall routine deletes that whole
directory: *"any data stored within it, such as settings or logs, will be lost"*
([Velopack uninstalling docs](https://docs.velopack.io/integrating/uninstalling)). Updates are
narrower — only the `current\` subdirectory is replaced — so **uninstall is the destructive
path, not update.**

This is why `--packId` in [`.github/workflows/release.yml`](../.github/workflows/release.yml) is
`NovaTerminalApp` and not `NovaTerminal`. Aligning it with the app name would make the install
root and the config root the same folder, and uninstalling would silently destroy every setting,
SSH profile, known-host entry, command history and workspace.

**Do not "tidy" the packId to match the app name.** `--packTitle` is what the user sees in
shortcuts and Add/Remove Programs; only nupkg file names and the install directory derive from
`--packId`.

### Changing the packId orphans installed clients, and does it silently

A packId is the app's identity to the updater, so it can only be changed before a release
ships. What makes this dangerous is *how* it fails — observed for real when 0.5.0 shipped
under a new packId while a tester had a 0.4.1 build installed under the old one:

- **The old client is still offered the new release.** `UpdateManager.CheckForUpdatesAsync`
  is a bare `feed.Where(Full).MaxBy(x => x.Version)` — it does **not** compare the feed's
  `PackageId` against the installed app's id. The client fetches the latest non-prerelease
  release's `releases.win.json`, sees a higher version, and raises an update toast across
  the packId boundary.
- **The update is not a no-op it can recover from.** Applying it leaves the client running
  new code out of the *old* install root — which, for a packId that matched the app name,
  is the config root. That machine is then permanently in the state this whole document
  exists to prevent, and no later update moves it out.
- **There is no in-band fix.** Deleting the old release does not help: the client reads the
  latest release's feed regardless of which release it was installed from. The only clean
  path is manual — back up the config root, uninstall, reinstall from the new `Setup.exe`,
  restore.

So a packId change is a **breaking, un-migratable** change for anyone already installed.
Before making one, confirm nobody has the old id installed — including from throwaway test
releases, which is exactly the case that got missed.

## Secrets live outside the config root

Passwords and passphrases are **not** in `%LocalAppData%\NovaTerminal`. They go to the OS
credential store, selected per platform in
[`SecretStore.cs`](../src/NovaTerminal.App/Shell/Secrets/SecretStore.cs):

- Windows — Win32 Credential Manager, per-user and DPAPI-protected
  ([`WindowsCredentialStore.cs`](../src/NovaTerminal.App/Shell/Secrets/WindowsCredentialStore.cs))
- macOS — Keychain (`MacKeychainStore`)
- Linux — Secret Service (`LinuxSecretStore`)

Consequences for anything that copies the config folder:

- **A folder copy silently omits every credential.** Restoring it elsewhere yields SSH profiles
  that look complete but cannot authenticate — a silent partial failure, not an error.
- **The Windows store cannot be enumerated.**
  [`Win32CredentialManager`](../src/NovaTerminal.App/Shell/Native/Win32CredentialManager.cs) binds
  only `CredReadW`, `CredWriteW` and `CredDeleteW` — there is no `CredEnumerateW`. You cannot ask
  "what secrets does NovaTerminal hold?"; you can only derive the key set from
  `ssh\profiles.json` and read each key by name.
- **Keys are namespaced** `NovaTerminal:...`, in practice `NovaTerminal:SSH:User@Host` or
  `NovaTerminal:SSH:ProfileName:User@Host` (`WindowsCredentialStore.ToTarget` / `ExtractUsername`).
- **DPAPI blobs are not portable** across users or machines, so a credential cannot be moved by
  copying ciphertext. Exporting secrets means reading plaintext out of the store and taking on
  responsibility for encrypting the export.

## Inventory

All paths are properties of
[`AppPaths`](../src/NovaTerminal.App/Shell/AppPaths.cs), relative to the config root:

| Path | Contents |
|---|---|
| `settings.json` | all app settings |
| `command-palette-usage.json` | command-palette ranking data |
| `themes\` | user-installed themes |
| `sessions\last_session.json` | restored tab/pane layout |
| `workspaces\`, `workspace_templates\` | saved and templated workspaces |
| `policy\workspace_policy.json` | workspace trust policy |
| `ssh\profiles.json` | SSH connection profiles (no secrets) |
| `ssh\native_known_hosts.json` | native-backend known hosts |
| `command-assist\history.jsonl` | append-only command history |
| `command-assist\snippets.json` | saved snippets |
| `recordings\` | terminal recordings |
| `logs\` | `debug.log`, `startup_error.txt`, `workspace_audit.log`, … |

## Constraints

**Resolve paths through `AppPaths`, never a hardcoded `%LocalAppData%\NovaTerminal`.**
`AppPaths.RootDirectory` honours the `NOVATERM_APPDATA_ROOT` environment variable as a root
override; tests and portable setups depend on it. Hardcoding the path makes a feature
untestable and breaks portable installs.

**Never write `vault.dat` back.** `AppPaths.LegacyVaultFilePath` is documented in source as
*"the pre-#100 weakly-encrypted vault file, kept only so it can be deleted."* A restore that
recreates it resurrects crypto that was deliberately removed.

**Never write `command-assist\history.json` back.** It is a one-time migration source only; the
store converts it to `history.jsonl` and renames it to `history.json.bak`. Restoring it can
re-trigger migration over newer data.

**Treat `command-assist\history.jsonl` and `recordings\` as sensitive.** Command lines
sometimes contain secrets, and recordings capture arbitrary terminal output. Any export intended
to be shareable should exclude them or say plainly that it does not. `logs\` is also bulk and
noise — usually not worth carrying.

**Anything written for recovery belongs under the config root.** Automatic snapshots, exported archives and rollback state must live under `AppPaths.RootDirectory` (or a user-chosen path), never under the install root — the install root is deleted on uninstall, which would take the recovery data with it.

**Expect files to be locked on Windows.** A running instance holds `logs\debug.log` open, so a
naive recursive copy of the config root fails partway. Either exclude `logs\`, or require the
app be closed.
