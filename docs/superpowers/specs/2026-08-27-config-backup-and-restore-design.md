# Configuration Backup and Restore — Design

Date: 2026-08-27
Status: Approved

## Problem

NovaTerminal keeps all user state under `%LOCALAPPDATA%\NovaTerminal\` — settings, themes,
connection profiles, workspaces, policy, snippets. There is no way to copy that state to a new
machine, roll it back after a bad edit, or hand a curated subset to a teammate. The only existing
import paths are the theme `Import…` button and the Windows Terminal profile importer, neither of
which covers the general case.

## Goals

1. **Migrate** — carry configuration to a new machine as a single portable file.
2. **Undo** — roll back a corrupted `settings.json`, a botched theme, or an accidental profile delete.
3. **Share** — export a subset that is safe to email or commit to a repo.

## Non-goals

- **Sync across machines.** Continuous two-way sync needs a sync target, change detection, and
  conflict resolution — a separate subsystem. It gets its own spec, built on the bundle format
  defined here.
- **Secret migration.** Passwords stay in the OS keychain and never enter a bundle. See [Secrets](#secrets).
- **Transcript backup.** Command history, session recordings, and last-session layout are excluded.

## Architecture

One module, `src/NovaTerminal.App/Shell/Backup/`, exposing a single service that every surface calls.
Nothing else in the app knows backups exist.

| Type | Responsibility |
| --- | --- |
| `BackupManifest` | Record: schema version, app version, created-at UTC, machine name, category list. |
| `BackupCatalog` | Static. The single mapping from category to files under `AppPaths`. |
| `BundleWriter` / `BundleReader` | Zip out, zip in. Reader validates the manifest before exposing entries. |
| `BackupService` | `Export`, `Inspect`, `Import`, `Snapshot`, `ListSnapshots`, `Restore`. |
| `SnapshotScheduler` | `FileSystemWatcher` over tracked paths → debounce → `Snapshot("auto")`. |

`BackupCatalog` is the critical seam. It is the only list of what gets backed up, so adding a
category is a one-line change and a drift-guard test can assert nothing escapes it. This mirrors how
`AppPaths` centralizes path construction today.

`BackupService` is constructed with a root directory rather than reading `AppPaths` statically, so
tests drive it against a temp tree.

## Bundle format

A zip with the `.novabackup` extension.

```
manifest.json
settings/settings.json
themes/*.json
connections/profiles.json
connections/native_known_hosts.json
workspaces/**
workspace_templates/**
policy/**
command-assist/snippets.json
```

```json
{
  "schemaVersion": 1,
  "appVersion": "1.4.2",
  "createdUtc": "2026-08-27T09:14:00Z",
  "machine": "DESKTOP-ABC",
  "categories": ["settings", "themes", "connections", "workspaces", "policy", "snippets"]
}
```

All paths inside the bundle are relative, so a bundle is machine-portable and diffable enough to
commit to a repo.

### Versioning

`schemaVersion` is checked on read:

- Equal to current → load.
- Lower → run registered migrations in order, same shape as `ISshProfileStoreMigration`. Empty at v1.
- Higher → refuse with a message naming the bundle's version and the app's.

### Contents

Included: `settings.json`, `themes/`, `ssh/profiles.json`, `ssh/native_known_hosts.json`,
`workspaces/`, `workspace_templates/`, `policy/`, `command-assist/snippets.json`.

Excluded: `logs/`, `recordings/`, `sessions/last_session.json`, `command-assist/history.jsonl`,
`command-palette-usage.json`, `backups/`. History and recordings are the most privacy-sensitive and largest
files in the tree; last-session references machine-local working directories that may not exist on
the target.

### Secrets

A bundle never contains secret material. Connection profiles keep their `rememberPassword` flag but
carry no password; import writes nothing to the keychain, and the user re-enters passwords once on
the target machine. This preserves the position taken in issue #100 — there is deliberately no
file-based secret storage — and keeps a bundle safe to email or commit.

A test asserts this structurally: after exporting a tree whose keychain holds a known sentinel
value, the bundle bytes are scanned and must not contain it.

## Snapshots

`AppPaths.BackupsDirectory` → `%LOCALAPPDATA%\NovaTerminal\backups\`, holding the same zip format
named `<reason>-<utcTimestamp>-<hash8>.novabackup`, where reason is `auto`, `pre-import`, or
`pre-restore`. The file name stem is the snapshot **id** used by `Restore(id)` and the CLI.

`BackupsDirectory` is itself excluded from `BackupCatalog` — snapshots never contain snapshots.

**Trigger.** A `FileSystemWatcher` over the tracked set coalesces changes across 30 seconds of quiet
and then writes one `auto` snapshot. Before writing, the bundle's content hash is compared against
the newest existing snapshot of any reason; identical content is not written, so idle installs and
no-op saves do not accumulate files.

**Forced snapshots.** A snapshot is taken immediately before any import or restore, with reason
`pre-import` or `pre-restore`. These skip the hash-dedupe check and are always written, so the
pre-state of a destructive operation is recorded even when it matches the newest auto snapshot.
Every destructive path is therefore one action from reversible.

**Retention.** Keep the union of the newest 20 snapshots and everything written in the last 7 days.
Prune on write.

**Restore.** Restoring a snapshot is always a **Replace** of the categories the snapshot contains —
a rollback, not a merge. Categories absent from the snapshot are untouched.

**Failure policy.** Snapshot failures are logged and swallowed. A failing backup must never block
the app or a settings save.

## Import

`Inspect(path)` reads the manifest only and returns categories with item counts, so the dialog can
show "3 themes, 7 connections, 12 snippets" before anything is touched.

The user then chooses a mode for the import:

- **Merge** — profiles and themes match by id (themes by name where no id exists). The bundle wins on
  conflict; local items with no counterpart survive. `settings.json` merges key by key, bundle
  winning per key.
- **Replace** — for each included category the bundle becomes the truth; local items in those
  categories are dropped. Categories absent from the bundle are untouched either way.

**Atomicity.** The import is staged into a temp tree and moved into place only after every category
has been written successfully. Individual file writes go through the existing `AtomicFile` helper, so
a crash mid-write cannot tear a destination. A partial or failed import leaves the original state
intact and reports which category failed.

## Surfaces

All four wrap the same `BackupService`.

**Settings window.** A "Backup & Restore" section: `Export…` and `Import…` buttons, plus a list of
snapshots showing timestamp, reason, and size with a `Restore` action per row. Restore asks for
confirmation first — naming the snapshot, saying which categories it replaces, and noting that a
pre-restore snapshot is taken so the restore itself can be undone. Recoverability is not a
substitute for asking, and Import already prompts.

**Command palette.** `Export configuration…`, `Import configuration…`, `Restore from snapshot…`.

**CLI.** A `BackupCommand` following the existing `IsSupportedCliMode` / `Execute` chain in
`src/NovaTerminal.Cli/Program.cs`:

```
backup export <path>
backup import <path> --merge | --replace
backup list
backup restore <id>
```

`backup list` prints each snapshot's id, reason, timestamp, and size; `<id>` is the file-name stem
shown by `list`. `--merge` and `--replace` are mutually exclusive and one is required — there is no
default import mode, since guessing wrong is destructive.

This also makes the whole feature exercisable without a window.

**MCP.** `novaterminal.backup_export` and `novaterminal.backup_list` only — no import, no restore.
The MCP server is an out-of-process helper whose existing tools are read-only schema and validation
helpers; letting an agent silently replace live connection profiles is a destructive action the user
never sees. Export-before-you-change is the useful half and carries no risk.

## Error handling

Every failure surfaces as a typed result, not an exception at the UI layer:

| Condition | Behavior |
| --- | --- |
| Unreadable or truncated zip | Refuse, name the file. |
| Missing or malformed manifest | Refuse as "not a NovaTerminal backup". |
| `schemaVersion` newer than app | Refuse, name both versions. |
| Category present in manifest but absent in zip | Refuse as corrupt. |
| Disk full / destination locked during import | Roll back to staged original, report failing category. |
| Snapshot write failure | Log, swallow, continue. |

## Testing

`NovaTerminal.App.Tests`, driven against a temp tree via the existing `NOVATERM_APPDATA_ROOT`
override. No new test project, so `ci.yml` needs no changes.

- Round trip (Replace mode, import into an empty tree): reproduces the original byte-for-byte per
  category. Merge does not hold to this: it re-serializes JSON files through `WriteIndented`, so a
  merged file's bytes differ from the source even when its content is identical — the better
  behavior (consistently formatted output), not a bug to fix.
- Merge semantics per category: conflicting id updated, local extra survives, settings merge by key.
- Replace semantics per category: local items in included categories dropped, excluded categories untouched.
- Corrupt zip, truncated zip, missing manifest, malformed manifest each refused with the right result.
- `schemaVersion` higher than current refused; lower runs migrations.
- Secret sentinel never appears in bundle bytes.
- Snapshot dedupe: unchanged content writes no second `auto` snapshot, but a `pre-import` snapshot
  is written even when its content matches the newest one.
- Retention pruning keeps the union of the count rule and the age rule.
- `BackupsDirectory` is never an entry in a bundle.
- Restoring a snapshot replaces its categories and leaves absent categories alone.
- A failed import leaves a `pre-import` snapshot and the original tree intact.
- Drift guard: every `AppPaths` member is either mapped in `BackupCatalog` or on an explicit
  exclusion list, so a future path cannot silently escape backup.

Tests must stay POSIX-portable — `App.Tests` also runs on ubuntu in CI, where a file lock does not
block rename or delete.

## Follow-up work

- **Sync across machines**, built on this bundle format: a sync target, change detection, and
  conflict resolution. Separate spec.
- **MCP import behind a settings toggle**, if agent-driven restore turns out to be wanted.
