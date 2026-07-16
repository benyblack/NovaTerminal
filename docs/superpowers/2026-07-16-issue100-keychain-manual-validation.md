# PR #132 (#100) — macOS/Linux keychain manual validation runbook

The merge gate for PR #132 is **native keychain behavior on macOS and Linux** — the weak
file crypto it replaces was Linux/macOS-only; Windows already used Credential Manager.
Windows was regression-checked separately (see the appendix) and shows no regression, but
that does **not** cover the gate. Run the checks below on real hardware before merging.

Target/attribute scheme (for verification commands):
- Service/label: `NovaTerminal`
- Key per credential: `SSH:PROFILE:<profile-guid>` or `SSH:<user@host>`
- App-data dir (where `settings.json` lives): `~/.local/share/NovaTerminal` on Linux;
  **`~/Library/Application Support/NovaTerminal` on macOS** (confirmed 2026-07-16).
  Overridable via `NOVATERM_APPDATA_ROOT`.

## Fastest keychain-store check (recommended, no SSH needed)

The SSH connect UI on this branch is 50 commits stale and its OpenSSH/Native path is
unreliable, so don't gate on a live connection. Instead run the shipped integration test,
which does a real `MacKeychainStore` / `LinuxSecretStore` write→update→read→delete round-trip:

```sh
scripts/build.sh test tests/NovaTerminal.App.Tests/NovaTerminal.App.Tests.csproj \
  --filter "FullyQualifiedName~KeychainSecretStoreIntegrationTests"
```

It **self-skips silently (counts as passed) when no keyring is present**, so for a real
verdict temporarily change the skip guard's `return;` to
`throw new System.InvalidOperationException("keychain unavailable");` — then a pass proves
it actually ran. **macOS: verified 2026-07-16 (passes with the throw-guard).**

## Linux — fresh-VM setup, then run (Ubuntu/Debian; adjust apt→dnf for Fedora)

```sh
# 1) System deps: build tools, Secret Service (libsecret), keyring, GUI runtime libs
sudo apt update
sudo apt install -y git curl build-essential pkg-config \
    libsecret-1-0 libsecret-1-dev libsecret-tools gnome-keyring dbus-x11 \
    libx11-6 libice6 libsm6 libfontconfig1 libgl1 libglib2.0-0

# 2) Rust toolchain (native PTY/SSH libs)
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y
. "$HOME/.cargo/env"

# 3) .NET SDK — preview channel (must satisfy repo global.json; check after install)
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --quality preview
export PATH="$HOME/.dotnet:$PATH"
dotnet --version    # compare against global.json's pinned SDK

# 4) Source + branch
git clone https://github.com/benyblack/NovaTerminal.git
cd NovaTerminal
git checkout fix/issue-100-vault-keychain-security
```

### Linux keychain-store round-trip (the key check)

`LinuxSecretStore` needs a live, **unlocked** Secret Service — so run this **inside the
graphical desktop session** (a terminal in the GNOME/KDE session), where `gnome-keyring`
provides the D-Bus secrets service and the login keyring is unlocked. Apply the same
throw-guard tweak (skip-guard `return;` → `throw new System.InvalidOperationException(...)`)
so a skip can't masquerade as a pass, then:

```sh
scripts/build.sh test tests/NovaTerminal.App.Tests/NovaTerminal.App.Tests.csproj \
  --filter "FullyQualifiedName~KeychainSecretStoreIntegrationTests"
```

- **Pass** → `LinuxSecretStore` write/update/read/delete works against Secret Service. ✔
- **Fails with "keychain unavailable"** → the Secret Service isn't reachable/unlocked; make
  sure you're in the desktop session (not a bare SSH/tty) with the login keyring unlocked.

Independently confirm an entry is visible (from the same session):
`secret-tool search service NovaTerminal` (after a save).

### Linux GUI checks (run the app)

```sh
dotnet src/NovaTerminal.App/bin/Debug/net10.0/NovaTerminal.dll
```

- **§3 startup probe:** with the keyring **unlocked**, launch — window appears promptly, no
  hang, no unexpected keyring-unlock dialog on startup (the `LinuxSecretStore` ctor does a
  synchronous Secret Service lookup on the UI thread).
- **§4 disabled-mode/locked-keyring:** launch with no secrets provider —
  `dbus-run-session -- dotnet src/NovaTerminal.App/bin/Debug/net10.0/NovaTerminal.dll` —
  expect the app to launch, show the one-time "Credential storage unavailable" toast, and
  still let SSH connect (password just isn't persisted). No crash, no silent swallow.
- **§6 legacy `vault.dat`:** `printf x > ~/.local/share/NovaTerminal/vault.dat`, launch,
  then confirm it's gone (`ls -l ~/.local/share/NovaTerminal/vault.dat` → No such file).

## 0. Build & run the PR branch

```sh
git fetch origin fix/issue-100-vault-keychain-security
git checkout fix/issue-100-vault-keychain-security
scripts/build.sh build src/NovaTerminal.App        # wrapper; or: dotnet build -nodeReuse:false
# run the built app (adjust net10.0 path if needed):
dotnet src/NovaTerminal.App/bin/Debug/net10.0/NovaTerminal.dll
```

Use a **password-based** SSH profile (not agent/key) — only password auth exercises the
keychain. Create a throwaway profile against any host that accepts password auth.

---

## 1. macOS — keychain round-trip

1. Connect the password profile; enter the password with **remember** on.
2. **Verify it was written:** Keychain Access → search `NovaTerminal` → an item whose
   service is `NovaTerminal` and account/key is `SSH:PROFILE:<guid>` exists.
   CLI equivalent: `security find-generic-password -s "NovaTerminal" -a "SSH:PROFILE:<guid>"`
3. Quit the app fully, relaunch, reconnect the same profile.

**Pass:** step 3 connects with **no** password prompt (read from Keychain); step 2 item present.
**Red flag:** re-prompts every time, or no Keychain item created.

## 2. Linux (GNOME Keyring / KWallet) — keychain round-trip

1. Same connect-with-remember as above.
2. **Verify write:** `secret-tool search key "SSH:PROFILE:<guid>"`
   (or `secret-tool search service NovaTerminal`) returns the item.
3. Quit fully, relaunch, reconnect.

**Pass:** step 3 no re-prompt; `secret-tool` shows the entry.
**Red flag:** nothing stored, or re-prompt on reconnect.

## 3. Linux — startup probe doesn't block or pop an unlock prompt

`LinuxSecretStore` does a **synchronous** Secret Service lookup on the UI thread in its
ctor. With an **unlocked** keyring, launch the app and watch startup.

**Pass:** window appears promptly; no hang; no unexpected keyring-unlock dialog at launch.
**Red flag:** app hangs on the splash/first window, or an unlock prompt appears just from
launching (before any connect).

## 4. Linux — headless / locked-keyring (disabled mode)

Simulate no usable keychain, e.g. run without a Secret Service provider:

```sh
# a session with no keyring daemon / D-Bus secrets service, or lock the keyring first
dbus-run-session -- dotnet src/NovaTerminal.App/bin/Debug/net10.0/NovaTerminal.dll
```

**Pass:** app launches; shows the one-time **"Credential storage unavailable"** toast;
SSH still **connects** (you enter the password each time; it just isn't persisted);
`VaultService.PersistenceAvailable` is false (reads null, writes no-op).
**Red flag:** crash on launch, silent swallow (no toast), or SSH broken entirely.

## 5. Cross-process — app-saved password read by `ssh-askpass`

With a password saved (from #1/#2), trigger a flow that invokes the `ssh-askpass` helper
(separate process) — e.g. a git/ssh op or reconnect path that shells out to askpass.

**Pass:** the helper reads the same keychain entry and authenticates without a manual prompt.
**Red flag:** askpass can't find the secret (prompts) although the app has it saved.

## 6. Legacy `vault.dat` deletion (Linux/macOS)

```sh
# with the app closed, plant a dummy in the app-data dir:
printf 'legacy junk' > ~/.local/share/NovaTerminal/vault.dat
ls -l ~/.local/share/NovaTerminal/vault.dat   # exists
# launch the app, then re-check:
ls -l ~/.local/share/NovaTerminal/vault.dat   # should be GONE
```

**Pass:** `vault.dat` is deleted on launch **without being read/migrated**.
**Red flag:** file still present, or its contents were parsed.

---

## Appendix — Windows results (2026-07-16, informational, not the gate)

Validated on a #132 worktree build (`ba4bea8`), Debug, Windows 11:
- Builds clean; launches with no crash (no new `startup_error.txt`).
- "Credentials stored in OS keychain" UI text renders (new store wired in).
- **Write:** creating profile `vw2` with a saved password created a fresh
  `NovaTerminal:SSH:PROFILE:<guid>` entry in Credential Manager, stamped that day
  (verified via `CredEnumerate` `LastWritten`).
- **Read across restart:** after a full app restart, `vw2` reconnected with no password
  prompt — password read back from Credential Manager.

Conclusion: the `ISecretStore` refactor did not regress the Windows Credential Manager
read/write path. macOS/Linux (sections 1–6) remain to be validated on real hardware.
