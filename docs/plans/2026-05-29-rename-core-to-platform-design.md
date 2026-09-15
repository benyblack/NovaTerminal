# Design: Rename `Ntilde.Core` → `Ntilde.Platform` and `App/Core/` → `App/Shell/`

**Date:** 2026-05-29
**Issue:** [#76](https://github.com/benyblack/ntilde/issues/76) — "Rename Ntilde.Core → Ntilde.Platform (the 'Core' name is overloaded three ways)"
**Status:** Approved design — ready for implementation plan

---

## Problem

"Core" means three different things in the repo, which is actively misleading:

1. **The `Ntilde.Core` assembly** (`src/Ntilde.Core/`) — actually a platform-utilities + SSH library (input routing, WSL path mapping, process abstraction, the SSH stack, the credential vault). It is **not** the terminal engine.
2. **The `App/Core/` folder** (`src/Ntilde.App/Core/`) — UI-shell glue (startup orchestration, app paths/logging/services, session & workspace managers, theme manager, command registry, profiles, the renderer view host), declaring `namespace Ntilde.Core`.
3. **The `Ntilde.Core` namespace** — used by *both* the assembly above *and* the `App/Core/` folder, so a `using Ntilde.Core;` pulls types from two different assemblies.

**Live hazard:** during the #74 GlobalHotkey crash fix, a change was nearly written to `src/Ntilde.Core/GlobalHotkey.cs` (the assembly) when the file actually lives at `src/Ntilde.App/Core/GlobalHotkey.cs` (the App folder, same namespace). It only failed because the `Ntilde.Core` assembly doesn't reference Avalonia, so it didn't compile — a silent mis-edit was one assembly reference away from landing in the wrong project.

This is tracked in `docs/ARCHITECTURE.md` §14 and the 2026-05-28 architecture review (section B).

---

## Goals / Acceptance

- Exactly **zero** meanings of "Core" remain in production assemblies and folders.
- A file path uniquely identifies which assembly a type compiles into.
- An architecture test enforces that no two assemblies share a namespace prefix.
- `scripts/build.ps1 build` and `scripts/build.ps1 test` are green; all existing arch tests still pass.

## Non-Goals (explicitly deferred to their own tracked plans)

- **Renderer extraction.** `App/Core/TerminalView.cs` and `TerminalDrawOperation.cs` → `Ntilde.Rendering` (§14). They stay in `App/Shell/` for now; their own plan moves them later.
- **SSH consolidation.** `Core/Ssh/` + `App/Core/{SftpService,VaultService,SshAskPassCommand}` → a future `Ntilde.Ssh`/`.Remote` (§14). The SSH stack stays in `Ntilde.Platform` for now.
- **No deep split-by-concern** of `App/Core/`. This is a faithful rename, not a re-architecture.

---

## Decision

### 1. Assembly: `Ntilde.Core` → `Ntilde.Platform`

The assembly is platform-integration utilities (input routing, path mapping, process abstraction, SSH, credential vault). "Platform" describes that accurately, and stays accurate after the future SSH carve-out leaves only platform glue behind.

Namespace map (prefix swap, structure preserved):

| Before | After |
|---|---|
| `Ntilde.Core` | `Ntilde.Platform` |
| `Ntilde.Core.Input` | `Ntilde.Platform.Input` |
| `Ntilde.Core.Execution` | `Ntilde.Platform.Execution` |
| `Ntilde.Core.Paths` | `Ntilde.Platform.Paths` |
| `Ntilde.Core.Ssh.{Interactions,Launch,Models,Native,OpenSsh,Sessions,Storage,Transport}` | `Ntilde.Platform.Ssh.{…}` |

### 2. `App/Core/` → `App/Shell/`, namespace `Ntilde.Core*` → `Ntilde.Shell*`

Single bucket named **Shell**. Rationale:
- `docs/ARCHITECTURE.md` §8 already titles the App assembly **"UI Shell"**. The folder *is* the shell's composition/glue layer.
- Fits the App assembly's existing flat namespace convention — the App root namespace is `Ntilde` with sibling buckets `Ntilde.Controls`, `Ntilde.Services`, `Ntilde.Models`, `Ntilde.ViewModels`. `Ntilde.Shell` slots in alongside them.
- **Not** `Ntilde.App`: the assembly is `Ntilde.App` but its root *namespace* is `Ntilde`; a `Ntilde.App` namespace inside it would recreate the same "name means two things" confusion.

Existing sub-namespace seams are preserved (no extra flattening):

| Before | After |
|---|---|
| `Ntilde.Core` (App/Core flat files) | `Ntilde.Shell` |
| `Ntilde.Core.Shortcuts` | `Ntilde.Shell.Shortcuts` |
| `Ntilde.Core.ThemeImporters` | `Ntilde.Shell.ThemeImporters` |
| `Ntilde.Core.Native` | `Ntilde.Shell.Native` |

### 3. Tests project: `Ntilde.Core.Tests` → `Ntilde.Platform.Tests`

Folder, `.csproj`, assembly name, and the test namespace move with the assembly under test.

### 4. New architecture invariant

Generalize the existing `Only_the_Core_assembly_uses_Ntilde_Core_namespace` rule into:

> Each leaf assembly (`VT`, `Replay`, `Rendering`, `Pty`, `Platform`) owns exactly its `Ntilde.<Name>.*` prefix, and **no other assembly** uses that prefix.

The App assembly retains the bare `Ntilde` root plus its app-specific buckets (`Ntilde.Shell`, `.Controls`, `.Services`, `.Models`, `.ViewModels`, `.Views`, `.UI`, `.CommandAssist`); the invariant asserts App does not reach into any leaf assembly's reserved prefix, and no leaf assembly uses another leaf's prefix or `Ntilde.Shell`.

---

## The disambiguation step (the one non-mechanical part)

Today `using Ntilde.Core;` resolves to a namespace whose types are split across **two** assemblies (the Platform assembly + the App/Core folder). After the rename those types live in two *distinct* namespaces (`Ntilde.Platform` and `Ntilde.Shell`). So every consumer's `using Ntilde.Core;` must be re-pointed to `Ntilde.Platform`, `Ntilde.Shell`, or **both**, depending on which types it actually references.

This cannot be a blind find-replace of `Ntilde.Core` → one target. Approach: rename the *definitions* first (App/Core files → `Ntilde.Shell*`, Core assembly files → `Ntilde.Platform*`), then let the compiler enumerate every now-broken `using`/reference and resolve each to the correct namespace(s). The build is the oracle.

---

## Concrete edit surface (non-worktree paths only)

**Assembly rename:**
- `src/Ntilde.Core/` → `src/Ntilde.Platform/` (folder + `Ntilde.Core.csproj` → `Ntilde.Platform.csproj`)
- All `.cs` under it: `namespace Ntilde.Core*` → `Ntilde.Platform*`
- `InternalsVisibleTo` in the csproj: `Ntilde.Core.Tests` → `Ntilde.Platform.Tests`
- `Ntilde.sln:24` and `.vs/nova2.slnx` project entry
- ProjectReferences: `src/Ntilde.App/Ntilde.App.csproj:219`, `tests/Ntilde.Architecture.Tests/...csproj:23`

**Tests project rename:**
- `tests/Ntilde.Core.Tests/` → `tests/Ntilde.Platform.Tests/` (folder + `.csproj` + assembly name + namespaces)
- `Ntilde.sln:28` and `.vs/nova2.slnx` project entry
- ProjectReference inside it (`...:23`) re-points to the renamed assembly

**App/Core rename:**
- `src/Ntilde.App/Core/` → `src/Ntilde.App/Shell/`
- ~50 files: `namespace Ntilde.Core{,.Shortcuts,.ThemeImporters,.Native}` → `Ntilde.Shell{…}`
- All in-assembly and cross-assembly consumers' `using` statements re-pointed (compiler-driven; see disambiguation step)

**Arch tests + docs:**
- `tests/Ntilde.Architecture.Tests/LayeringTests.cs:12` (`Core` accessor type + assembly name)
- `tests/Ntilde.Architecture.Tests/NamespaceAlignmentTests.cs` (rename/generalize the Core rule; add Platform alignment fact + the cross-assembly invariant)
- `docs/ARCHITECTURE.md` §7 title/body, §8 (note `App/Shell`), §12 (rule list), §13 (test table: `Ntilde.Platform.Tests`), §14 (remove/retire the rename tech-debt entry)

---

## Verification

1. `scripts/build.ps1 build` — clean (the disambiguation work is "done" when this is green).
2. `scripts/build.ps1 test` — all suites pass, including the new/generalized arch tests.
3. `rtk grep -rn "Ntilde.Core" src tests` returns **zero** hits outside `.claude/worktrees/`, `bin/`, `obj/`.
4. No folder or assembly named "Core" remains under `src/` or `tests/`.

## Risks / Notes

- **Worktrees:** `.claude/worktrees/{shortcuts-palette,startup-metrics-baseline,startup-orchestrator}` contain stale copies of `Ntilde.Core`. Out of scope — do **not** touch them; they're separate checkouts.
- **`obj/` AssemblyInfo:** generated `obj/.../*.AssemblyInfo.cs` files reference the old names; they regenerate on build. No manual edits.
- **Large mechanical diff** (~100+ files). Gated entirely by the existing arch tests + full build, so regressions surface immediately.
