# Rebrand: NovaTerminal → Ntilde

Date: 2026-09-15
Status: designed (not implemented)
Touches: every project, the packaging lanes (`2026-08-24-windows-installer-velopack-design.md`,
`2026-08-28-macos-installer-velopack-design.md`, `2026-09-02-linux-packaging-design.md`),
the Homebrew tap lane, the GitHub Pages site, and the MCP dev companion.

## Summary

Rename the product from **NovaTerminal** to **Ntilde** everywhere a user, a package
registry, or a build lane can see it, and rename the code identifiers to match so the
tree carries one name. The rename is a full one: solution, project folders, namespaces,
assembly names, MCP tool prefix, CLI command, env vars, file extensions, packaging
manifests, release asset names, docs, and site.

Existing installs get a one-time copy of their settings into the new data folder.
Everything else (secrets, the in-app updater's identity, package registry listings)
starts fresh and is documented in release notes.

## Decisions (already made with the owner)

| Question | Decision |
|---|---|
| Depth | Full rename: code identifiers as well as user-facing names. |
| Casing | `Ntilde` in C# and display strings; `ntilde` for CLI, packages, folders, MCP tools; `NTILDE_*` for env vars. |
| Existing installs | One-time settings-folder copy. Secrets and the updater start fresh. |
| GitHub repo | Owner renames it to `benyblack/ntilde`; all references move to the new slug. |
| Short name "nova" | Every user-visible use is renamed. The SSH FFI boundary (`NovaSsh*`, `nova_ssh_*`, `NOVA_SSH_RESULT_*`) and the Rust crate names (`rusty_pty`, `rusty_ssh`) keep their names. |
| Execution | One PR: a mechanical first commit produced by a throwaway script, then small hand-written judgment commits. Work happens in a dedicated worktree. |

## Blast radius (measured on main at c024293)

| What | Count |
|---|---|
| Tracked files with `NovaTerminal` in their path | 1002 |
| Tracked files containing `NovaTerminal` | 1148 |
| Occurrences of `NovaTerminal` in tracked text | ~10,500 |
| Directories: tests / src / docs / site / packaging / .github | 466 / 446 / 179 / 17 / 15 / 8 files |

Identifiers with external or persisted consequences: GitHub repo URL; Homebrew cask
`novaterminal`; winget `benyblack.NovaTerminal`; deb package `novaterminal` and
`/usr/bin/nova`; macOS bundle `com.benyblack.NovaTerminal`; Keychain service
`NovaTerminal`; per-user data folder `LocalAppData\NovaTerminal`; Velopack pack ID
`NovaTerminalApp`; MCP tool prefix `novaterminal.`; env vars `NOVATERM_*` and
`NOVATERMINAL_REPO_ROOT`; shell-integration scripts `nova-*` and functions `__nova_*`;
file extensions `.novabackup`, `.novarec`, `.novaws`.

## 1. Identifier mapping

Replacement order is longest token first so no token is half-replaced.

| Old | New | Where |
|---|---|---|
| `NovaTerminalApp` | `NtildeApp` | Velopack `--packId` in ci.yml and release.yml; the derived bundle-ID comments |
| `NovaTerminal` | `Ntilde` | `.sln`, project folders, `.csproj` files, namespaces, assembly names, window title, Keychain service, display strings, docs, plans, specs |
| `NOVATERMINAL_REPO_ROOT` | `NTILDE_REPO_ROOT` | MCP server repo-root override |
| `NOVATERM_*` | `NTILDE_*` | `NTILDE_APPDATA_ROOT`, `NTILDE_RENDER_METRICS`, `NTILDE_RENDER_METRICS_OUT`, `NTILDE_ENABLE_DOCKER_E2E`, and every other `NOVATERM_` var |
| `novaterminal` | `ntilde` | cask name, deb package, `.desktop` file, icon name, MCP tool prefix (`ntilde.backup_export`), lintian overrides, GitHub slug `benyblack/ntilde`, sidecar dir |
| `nova` (CLI command) | `ntilde` | deb symlink `/usr/bin/ntilde`, manpage `ntilde.1`, `ntilde.desktop`, `x-terminal-emulator` alternative, manpage prose, command catalogue origin tag |
| `nova-shell-integration.*`, `nova-install*`, `__nova_*` | `ntilde-shell-integration.*`, `ntilde-install*`, `__ntilde_*` | shell-integration assets and the code that emits, embeds, or looks for them |
| `.novabackup`, `.novarec`, `.novaws` | `.ntildebackup`, `.ntilderec`, `.ntildews` | new files are written with the new extension; readers accept both (see §2) |
| `nova_icon.ico`, `nova_icon.png` | `ntilde_icon.ico`, `ntilde_icon.png` | same image bytes for now; renamed only |
| `NovaTerminal-sidecar` | `ntilde-sidecar` | `scripts/run-sidecar.ps1` and `docs/mcp-dev-companion.md` |
| `com.novaterminal.Vault` | `com.ntilde.Vault` | secret-store attributes (fresh start; see §2) |

**Deliberately untouched:** `NovaSsh*` C# types, `nova_ssh_*` Rust exports,
`NOVA_SSH_RESULT_*` constants, the `rusty_pty` / `rusty_ssh` crate names and the
`.nova-rust-inputs.sha256` stamp file, the local checkout folder name, git history, and
any text inside `git log` output quoted in docs.

**Velopack invariant.** release.yml's own comment explains that `--packId` must not
equal the per-user data folder name, because Velopack's uninstall deletes
`%LocalAppData%\<packId>`. With the data folder `ntilde` and the pack ID `NtildeApp` the
invariant holds. `Ntilde` alone would NOT be safe: Windows paths are case-insensitive.
The comment is rewritten for the new names, not deleted.

## 2. Persisted-data migration

`AppPaths.EnsureInitialized` already contains a copy-based migration (newer-file-wins,
best effort, never deletes the source) for the pre-LocalAppData layouts. The rebrand
migration follows the same pattern.

### Data folder

- New root: `LocalAppData\ntilde` on Windows, `~/.local/share/ntilde` on macOS and
  Linux (both via `SpecialFolder.LocalApplicationData`).
- On init, if the marker file `.migrated-from-novaterminal` is absent from the new root
  and the old `NovaTerminal` root exists, every top-level file and subdirectory of the
  old root is copied with the existing `MigrateFileIfNeeded` /
  `MigrateDirectoryIfNeeded` helpers, except `logs`. The marker is then written. The old
  folder is left in place.
- The copy logic lives in a static `AppPaths.MigrateLegacyRoot(string legacyRoot,
  string newRoot)` so it can be unit-tested with temp directories; `EnsureInitialized`
  calls it with the real paths.
- `AgentHostDiscovery` (AgentHost.Contracts) and `BackupTools` (McpServer) simply take
  the new folder name. The discovery file is written by the running app and the backup
  tool reads from the live folder, so neither needs migration.

### Env var override

`NTILDE_APPDATA_ROOT` replaces `NOVATERM_APPDATA_ROOT`. No fallback read of the old
name: it is a dev, test, and portable-install knob. Release notes call it out.

### File extensions

- `BackupService.BundleExtension` becomes `.ntildebackup`; recordings and workspaces
  write `.ntilderec` and `.ntildews`.
- Every reader accepts both spellings: backup import, replay open, workspace load and
  the `suggestedName` stripping in `MainWindow`, and the file-open dialog filters list
  both patterns. No existing user file is renamed.

### Secrets

Keychain service (`MacKeychainStore.ServiceName`), the Windows Credential Manager
target prefix, and the Linux secret-service attributes all take the new name with no
fallback read. Stored SSH passwords and vault entries start fresh. Release notes say so.

### In-app updater

Pack ID becomes `NtildeApp`, so installs of the old app never see the rebranded release
as an update. Release notes tell users to install fresh and that settings carry over
automatically on first launch.

## 3. Packaging and release identity

- **Release assets:** `ntilde-<rid>-v<ver>.zip`, `ntilde-Setup-win-x64-v<ver>.exe`,
  `ntilde-linux-<arch>-v<ver>.AppImage`, `ntilde_<ver>-1_<arch>.deb`. The release
  workflow, the CI artifact path lists, and the smoke-test steps that look for
  `NovaTerminal.exe` / `NovaTerminal.app` move to `Ntilde.exe` / `Ntilde.app`.
- **Velopack:** `--packId NtildeApp`, `--mainExe Ntilde.exe`,
  `--bundleId com.benyblack.Ntilde`, `--packTitle Ntilde`, `--icon .../ntilde_icon.ico`.
- **Homebrew:** `packaging/homebrew/Casks/ntilde.rb` with `cask "ntilde"`,
  `name "Ntilde"`, `app "Ntilde.app"`, URLs under `benyblack/ntilde`. The tap repo stays
  `benyblack/homebrew-tap`; the release lane pushes `Casks/ntilde.rb`. The old
  `novaterminal.rb` inside the tap is a manual tap-side decision (§4).
- **winget:** new manifests under `packaging/winget/<ver>/benyblack.ntilde.*.yaml` with
  `PackageIdentifier: benyblack.ntilde`, `PackageName: Ntilde`, `Moniker: ntilde`.
  winget cannot rename an identifier, so this is a fresh first-time submission through
  the existing `submit-first-time.ps1`; the old `benyblack.NovaTerminal` listing ages
  out. The `0.3.0` manifests are deleted from the repo because they describe a package
  this repo no longer builds. `submit_winget` in release.yml targets the new identifier.
- **Debian:** package `ntilde`, install root `/usr/lib/ntilde`, binary
  `/usr/lib/ntilde/Ntilde`, symlink `/usr/bin/ntilde`, `ntilde.desktop`, `ntilde.1`,
  icon `ntilde.png`, lintian overrides file renamed. Add `Replaces: novaterminal` and
  `Conflicts: novaterminal` so apt upgrades cleanly from the old package.
- **GitHub Pages site:** `DEFAULT_BASE` in `site/astro.config.mjs` becomes `/ntilde`.
  Site content, favicon alt text, and README badges follow the mechanical replacement.
- **MCP server:** tool names become `ntilde.<tool>`. The drift-guard tests that pin tool
  names and settings fields are updated in the same commit.
- **Dev tooling:** `run-sidecar.ps1` mirrors to `ntilde-sidecar`; `build.ps1` and
  `build.sh` kill stale `Ntilde.McpServer` processes; the `.mcp.json` sample in
  `docs/mcp-dev-companion.md` points at the new DLL.

## 4. Manual steps (owner), sequencing, verification

### Owner-only steps, in order

1. Merge or close the open Homebrew tap PR (`packaging/homebrew-tap`) so the rebrand
   branches from a main that already has the cask lane. If it lands after the rebrand
   branch is cut, the rebrand branch merges main and reapplies the mapping to the new
   files.
2. Rename the GitHub repo to `benyblack/ntilde` before the rebrand PR opens. GitHub
   redirects the old URL, so nothing breaks in the gap.
3. After the PR merges: in `benyblack/homebrew-tap`, delete `Casks/novaterminal.rb` or
   leave it pinned to the last old release.
4. If the Pages repo variable `ASTRO_BASE` is set, update it; the default moves to
   `/ntilde` on its own.
5. Locally, update the `novaterminal` MCP server entry in `~/.claude.json` to the new
   sidecar path and DLL name. Renaming the local checkout folder is optional.
6. On the first stable tag after the rebrand, run the winget first-time submission for
   `benyblack.ntilde`.

### Commit sequence inside the PR

1. **Mechanical sweep** from a throwaway script kept in the session scratchpad (not
   committed). `git mv` for every path containing a mapped token, then ordered token
   replacement across tracked text files, with the §1 exclude list applied. Binary files
   are renamed only. Reviewers verify this commit by re-running the sweep on its parent
   and diffing, not by reading it.
2. **Data-folder migration** (`MigrateLegacyRoot`, marker file, call site) with unit
   tests.
3. **Dual-extension acceptance** for backups, recordings, workspaces, with tests.
4. **Packaging judgment calls:** deb `Replaces`/`Conflicts`, new winget manifests and
   deletion of the old ones, rewritten Velopack comments, release-notes entry.
5. **Guard-test and prose fix-ups** the sweep could not do: sentences that now read
   oddly, the `nova(1)` manpage prose, the command-catalogue attribution string.

### Verification before completion

- Solution builds via `scripts/build.ps1` with no missing-file warnings.
- Green locally: `Ntilde.Architecture.Tests` (namespace alignment),
  `Ntilde.McpServer.Tests` (drift guards), `Ntilde.App.Tests` (settings whitelist,
  migration, extension tests; run with `--blame-hang-timeout 5m`, never concurrently),
  `Ntilde.Core.Tests`.
- `git grep -i nova` returns only the §1 exclude list and quoted git-history text. The
  output goes in the PR description.
- `packaging/linux/build-deb.sh` smoke test on the renamed layout if a Linux runner is
  at hand; otherwise the CI deb lane is the check.
- The cask passes `ruby -c` after placeholder substitution, as the release lane does.
- PR CI fully green. Known flakes (memory: unit-test host hang, cargo dep fetch, PTY
  backspace) get one re-run, not a debug session.

## Amendments (2026-09-15, during planning)

1. The Docker SSH e2e fixture (`tests/.../NativeSsh/Dockerfile`, user `nova`, prompt `nova$`,
   image `novaterm-native-ssh-e2e`) is excluded from the rename. 25 byte-exact `.rec` fixtures
   and the parity tests assert the recorded prompt literally.
2. `packaging/arch` (AUR, `novaterminal-bin`) is on main and joins the rename: `ntilde-bin`,
   `provides=('ntilde')`, `conflicts=('ntilde' 'novaterminal' 'novaterminal-bin')`,
   `replaces=('novaterminal-bin')`.
3. winget: the `0.3.0` manifests are deleted and replaced by `packaging/winget/template/`
   with `__VERSION__` / `__SHA256__` placeholders, rendered at the first Ntilde release. A
   manifest for an unpublished asset cannot carry a real hash.
4. `novarec` is the replay header `type` field, not a file extension. New recordings write
   `ntilderec`; readers accept both tokens.
5. Release asset prefix is lowercase `ntilde-` (`ntilde-win-x64-v1.2.3.zip`); Velopack nupkgs
   follow the pack ID: `NtildeApp-1.2.3-full.nupkg`.
6. Homebrew PR #453 has not merged. The rebrand branch merges main after it lands and re-runs
   the sweep over the new files.
7. The mechanical work lands as two commits (path renames, then text) so rename detection and
   `git log --follow` keep working.

## Risks

| Risk | Mitigation |
|---|---|
| A token inside the SSH FFI or crate names gets renamed and breaks the native ABI | Exclude list in the sweep script; `git grep` audit after the sweep must show those names unchanged |
| Half-replaced token (`NtildeApp` vs `Ntilde`) | Longest-first ordering; audit grep for `NovaTerminal` and `novaterminal` must return zero hits |
| The sweep runs over a dirty tree and mixes unrelated edits into the mechanical commit | Sweep runs only from a clean worktree; `git status` must be empty before it starts |
| A `NOVATERM_*` variable is missed because the sweep only knows the ones listed here | The sweep matches the `NOVATERM_` prefix, not a fixed list; all 15 current names are covered |
| Velopack pack ID equals data folder on Windows | `NtildeApp` vs `ntilde`; comment retained |
| Users lose SSH passwords | Documented in release notes; settings themselves migrate |
| Homebrew tap PR conflicts | Cut the branch after it merges, or merge main and re-run the sweep on the new files |
