# Design: Rename `NovaTerminal.Platform` → `NovaTerminal.Platform` and `App/Core/` → `App/Shell/`

**Date:** 2026-05-29
**Issue:** [#76](https://github.com/benyblack/NovaTerminal/issues/76) — "Rename NovaTerminal.Platform → NovaTerminal.Platform (the 'Core' name is overloaded three ways)"
**Status:** Approved design — ready for implementation plan

---

## Problem

"Core" means three different things in the repo, which is actively misleading:

1. **The `NovaTerminal.Platform` assembly** (`src/NovaTerminal.Platform/`) — actually a platform-utilities + SSH library (input routing, WSL path mapping, process abstraction, the SSH stack, the credential vault). It is **not** the terminal engine.
2. **The `App/Core/` folder** (`src/NovaTerminal.App/Core/`) — UI-shell glue (startup orchestration, app paths/logging/services, session & workspace managers, theme manager, command registry, profiles, the renderer view host), declaring `namespace NovaTerminal.Platform`.
3. **The `NovaTerminal.Platform` namespace** — used by *both* the assembly above *and* the `App/Core/` folder, so a `using NovaTerminal.Platform;` pulls types from two different assemblies.

**Live hazard:** during the #74 GlobalHotkey crash fix, a change was nearly written to `src/NovaTerminal.Platform/GlobalHotkey.cs` (the assembly) when the file actually lives at `src/NovaTerminal.App/Core/GlobalHotkey.cs` (the App folder, same namespace). It only failed because the `NovaTerminal.Platform` assembly doesn't reference Avalonia, so it didn't compile — a silent mis-edit was one assembly reference away from landing in the wrong project.

This is tracked in `docs/ARCHITECTURE.md` §14 and the 2026-05-28 architecture review (section B).

---

## Goals / Acceptance

- Exactly **zero** meanings of "Core" remain in production assemblies and folders.
- A file path uniquely identifies which assembly a type compiles into.
- An architecture test enforces that no two assemblies share a namespace prefix.
- `scripts/build.ps1 build` and `scripts/build.ps1 test` are green; all existing arch tests still pass.

## Non-Goals (explicitly deferred to their own tracked plans)

- **Renderer extraction.** `App/Core/TerminalView.cs` and `TerminalDrawOperation.cs` → `NovaTerminal.Rendering` (§14). They stay in `App/Shell/` for now; their own plan moves them later.
- **SSH consolidation.** `Core/Ssh/` + `App/Core/{SftpService,VaultService,SshAskPassCommand}` → a future `NovaTerminal.Ssh`/`.Remote` (§14). The SSH stack stays in `NovaTerminal.Platform` for now.
- **No deep split-by-concern** of `App/Core/`. This is a faithful rename, not a re-architecture.

---

## Decision

### 1. Assembly: `NovaTerminal.Platform` → `NovaTerminal.Platform`

The assembly is platform-integration utilities (input routing, path mapping, process abstraction, SSH, credential vault). "Platform" describes that accurately, and stays accurate after the future SSH carve-out leaves only platform glue behind.

Namespace map (prefix swap, structure preserved):

| Before | After |
|---|---|
| `NovaTerminal.Platform` | `NovaTerminal.Platform` |
| `NovaTerminal.Platform.Input` | `NovaTerminal.Platform.Input` |
| `NovaTerminal.Platform.Execution` | `NovaTerminal.Platform.Execution` |
| `NovaTerminal.Platform.Paths` | `NovaTerminal.Platform.Paths` |
| `NovaTerminal.Platform.Ssh.{Interactions,Launch,Models,Native,OpenSsh,Sessions,Storage,Transport}` | `NovaTerminal.Platform.Ssh.{…}` |

### 2. `App/Core/` → `App/Shell/`, namespace `NovaTerminal.Platform*` → `NovaTerminal.Shell*`

Single bucket named **Shell**. Rationale:
- `docs/ARCHITECTURE.md` §8 already titles the App assembly **"UI Shell"**. The folder *is* the shell's composition/glue layer.
- Fits the App assembly's existing flat namespace convention — the App root namespace is `NovaTerminal` with sibling buckets `NovaTerminal.Controls`, `NovaTerminal.Services`, `NovaTerminal.Models`, `NovaTerminal.ViewModels`. `NovaTerminal.Shell` slots in alongside them.
- **Not** `NovaTerminal.App`: the assembly is `NovaTerminal.App` but its root *namespace* is `NovaTerminal`; a `NovaTerminal.App` namespace inside it would recreate the same "name means two things" confusion.

Existing sub-namespace seams are preserved (no extra flattening):

| Before | After |
|---|---|
| `NovaTerminal.Platform` (App/Core flat files) | `NovaTerminal.Shell` |
| `NovaTerminal.Platform.Shortcuts` | `NovaTerminal.Shell.Shortcuts` |
| `NovaTerminal.Platform.ThemeImporters` | `NovaTerminal.Shell.ThemeImporters` |
| `NovaTerminal.Platform.Native` | `NovaTerminal.Shell.Native` |

### 3. Tests project: `NovaTerminal.Platform.Tests` → `NovaTerminal.Platform.Tests`

Folder, `.csproj`, assembly name, and the test namespace move with the assembly under test.

### 4. New architecture invariant

Generalize the existing `Only_the_Core_assembly_uses_NovaTerminal_Core_namespace` rule into:

> Each leaf assembly (`VT`, `Replay`, `Rendering`, `Pty`, `Platform`) owns exactly its `NovaTerminal.<Name>.*` prefix, and **no other assembly** uses that prefix.

The App assembly retains the bare `NovaTerminal` root plus its app-specific buckets (`NovaTerminal.Shell`, `.Controls`, `.Services`, `.Models`, `.ViewModels`, `.Views`, `.UI`, `.CommandAssist`); the invariant asserts App does not reach into any leaf assembly's reserved prefix, and no leaf assembly uses another leaf's prefix or `NovaTerminal.Shell`.

---

## The disambiguation step (the one non-mechanical part)

Today `using NovaTerminal.Platform;` resolves to a namespace whose types are split across **two** assemblies (the Platform assembly + the App/Core folder). After the rename those types live in two *distinct* namespaces (`NovaTerminal.Platform` and `NovaTerminal.Shell`). So every consumer's `using NovaTerminal.Platform;` must be re-pointed to `NovaTerminal.Platform`, `NovaTerminal.Shell`, or **both**, depending on which types it actually references.

This cannot be a blind find-replace of `NovaTerminal.Platform` → one target. Approach: rename the *definitions* first (App/Core files → `NovaTerminal.Shell*`, Core assembly files → `NovaTerminal.Platform*`), then let the compiler enumerate every now-broken `using`/reference and resolve each to the correct namespace(s). The build is the oracle.

---

## Concrete edit surface (non-worktree paths only)

**Assembly rename:**
- `src/NovaTerminal.Platform/` → `src/NovaTerminal.Platform/` (folder + `NovaTerminal.Platform.csproj` → `NovaTerminal.Platform.csproj`)
- All `.cs` under it: `namespace NovaTerminal.Platform*` → `NovaTerminal.Platform*`
- `InternalsVisibleTo` in the csproj: `NovaTerminal.Platform.Tests` → `NovaTerminal.Platform.Tests`
- `NovaTerminal.sln:24` and `.vs/nova2.slnx` project entry
- ProjectReferences: `src/NovaTerminal.App/NovaTerminal.App.csproj:219`, `tests/NovaTerminal.Architecture.Tests/...csproj:23`

**Tests project rename:**
- `tests/NovaTerminal.Platform.Tests/` → `tests/NovaTerminal.Platform.Tests/` (folder + `.csproj` + assembly name + namespaces)
- `NovaTerminal.sln:28` and `.vs/nova2.slnx` project entry
- ProjectReference inside it (`...:23`) re-points to the renamed assembly

**App/Core rename:**
- `src/NovaTerminal.App/Core/` → `src/NovaTerminal.App/Shell/`
- ~50 files: `namespace NovaTerminal.Platform{,.Shortcuts,.ThemeImporters,.Native}` → `NovaTerminal.Shell{…}`
- All in-assembly and cross-assembly consumers' `using` statements re-pointed (compiler-driven; see disambiguation step)

**Arch tests + docs:**
- `tests/NovaTerminal.Architecture.Tests/LayeringTests.cs:12` (`Core` accessor type + assembly name)
- `tests/NovaTerminal.Architecture.Tests/NamespaceAlignmentTests.cs` (rename/generalize the Core rule; add Platform alignment fact + the cross-assembly invariant)
- `docs/ARCHITECTURE.md` §7 title/body, §8 (note `App/Shell`), §12 (rule list), §13 (test table: `NovaTerminal.Platform.Tests`), §14 (remove/retire the rename tech-debt entry)

---

## Verification

1. `scripts/build.ps1 build` — clean (the disambiguation work is "done" when this is green).
2. `scripts/build.ps1 test` — all suites pass, including the new/generalized arch tests.
3. `rtk grep -rn "NovaTerminal.Platform" src tests` returns **zero** hits outside `.claude/worktrees/`, `bin/`, `obj/`.
4. No folder or assembly named "Core" remains under `src/` or `tests/`.

## Risks / Notes

- **Worktrees:** `.claude/worktrees/{shortcuts-palette,startup-metrics-baseline,startup-orchestrator}` contain stale copies of `NovaTerminal.Platform`. Out of scope — do **not** touch them; they're separate checkouts.
- **`obj/` AssemblyInfo:** generated `obj/.../*.AssemblyInfo.cs` files reference the old names; they regenerate on build. No manual edits.
- **Large mechanical diff** (~100+ files). Gated entirely by the existing arch tests + full build, so regressions surface immediately.
