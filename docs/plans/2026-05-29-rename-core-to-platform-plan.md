# Rename `NovaTerminal.Core` → `NovaTerminal.Platform` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the three-way "Core" name overload by renaming the `NovaTerminal.Core` assembly to `NovaTerminal.Platform` and the `src/NovaTerminal.App/Core/` folder to `src/NovaTerminal.App/Shell/` (namespace `NovaTerminal.Shell`), and enforce the separation with an architecture test.

**Architecture:** Two-phase rename that keeps the build green at every commit. **Phase 1** renames the App-side folder/namespace first (`NovaTerminal.Core*` → `NovaTerminal.Shell*`), which removes the cross-assembly namespace *collision* — after it, the `NovaTerminal.Core` namespace belongs solely to the production assembly. **Phase 2** is then an unambiguous repo-wide token replace `NovaTerminal.Core` → `NovaTerminal.Platform` (assembly + its test project together). The compiler/build is the oracle for the one genuinely non-mechanical step: re-pointing each ambiguous `using NovaTerminal.Core;` to the right namespace.

**Tech Stack:** .NET 10 / C#, MSBuild, xUnit.v3, NetArchTest.Rules. All builds/tests go through `scripts/build.ps1` (PowerShell wrapper that passes args to `dotnet` with `-nodeReuse:false`).

**Spec:** `docs/plans/2026-05-29-rename-core-to-platform-design.md`

---

## File Structure (what changes)

- `src/NovaTerminal.App/Core/` → `src/NovaTerminal.App/Shell/` — ~50 files, namespaces `NovaTerminal.Core{,.Shortcuts,.ThemeImporters,.Native}` → `NovaTerminal.Shell{…}`
- `src/NovaTerminal.Core/` → `src/NovaTerminal.Platform/` + `NovaTerminal.Core.csproj` → `NovaTerminal.Platform.csproj`; namespaces `NovaTerminal.Core{,.Input,.Execution,.Paths,.Ssh.*}` → `NovaTerminal.Platform{…}`
- `tests/NovaTerminal.Core.Tests/` → `tests/NovaTerminal.Platform.Tests/` + csproj rename
- Consumers: `src/NovaTerminal.App/**`, `src/NovaTerminal.Cli/**`, `tests/**` — `using`/`clr-namespace`/ProjectReference fixes
- `NovaTerminal.sln` — two project entries (paths/names; GUIDs unchanged)
- `tests/NovaTerminal.Architecture.Tests/{LayeringTests,NamespaceAlignmentTests}.cs` — generalized invariant
- `docs/ARCHITECTURE.md` — §7, §8, §12, §13, §14

**Out of scope (deferred to their own tracked plans):** renderer extraction (`TerminalView`/`TerminalDrawOperation` → Rendering), SSH consolidation, any split-by-concern of the Shell folder, `.claude/worktrees/*` (stale separate checkouts — never touch), `.vs/nova2.slnx` (not git-tracked).

---

## Conventions used in every command

- Run from repo root `D:\projects\nova2`.
- Replacement helper (PowerShell 7, UTF-8 no BOM, preserves trailing newline):

```powershell
function Replace-InFiles($files, $from, $to) {
  foreach ($f in $files) {
    $raw = Get-Content -LiteralPath $f.FullName -Raw
    $new = $raw.Replace($from, $to)
    if ($new -ne $raw) { Set-Content -LiteralPath $f.FullName -Value $new -NoNewline -Encoding utf8 }
  }
}
```

> `.Replace()` is a literal (non-regex) string replace — no escaping needed.

---

## Task 0: Branch and check in the approved design

**Files:**
- Modify: git branch only (design doc already exists at `docs/plans/2026-05-29-rename-core-to-platform-design.md`)

- [ ] **Step 1: Create the feature branch**

```powershell
rtk git checkout -b feature/issue-76-rename-core-to-platform
```

- [ ] **Step 2: Commit the design + plan docs**

```powershell
rtk git add docs/plans/2026-05-29-rename-core-to-platform-design.md docs/plans/2026-05-29-rename-core-to-platform-plan.md
rtk git commit -m @'
docs(rename): add design + plan for NovaTerminal.Core -> NovaTerminal.Platform (#76)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

- [ ] **Step 3: Establish a green baseline**

Run: `scripts/build.ps1 build NovaTerminal.sln`
Expected: build succeeds (0 errors). If it fails, stop — the tree was already broken; fix or report before renaming.

---

## Task 1: Rename `App/Core/` → `App/Shell/` (namespace `NovaTerminal.Shell`)

This phase removes the cross-assembly namespace collision. After it, only the production assembly uses `NovaTerminal.Core`.

**Files:**
- Move: `src/NovaTerminal.App/Core/` → `src/NovaTerminal.App/Shell/` (~50 `.cs`)
- Modify (declarations): every `.cs` under the moved folder
- Modify (App-local sub-namespace references): `src/NovaTerminal.App/**`, `tests/NovaTerminal.App.Tests/**`
- Modify (XAML): `src/NovaTerminal.App/App.axaml`, `Controls/TerminalPane.axaml`, `Controls/TransferCenter.axaml`, `UI/Replay/ReplayWindow.axaml`
- Modify (consumers, compiler-driven): wherever the build flags missing types

- [ ] **Step 1: Move the folder with git**

```powershell
rtk git mv src/NovaTerminal.App/Core src/NovaTerminal.App/Shell
```

- [ ] **Step 2: Rewrite namespace declarations in the moved folder**

```powershell
$shell = Get-ChildItem -Path src/NovaTerminal.App/Shell -Recurse -Filter *.cs
Replace-InFiles $shell 'namespace NovaTerminal.Core' 'namespace NovaTerminal.Shell'
```

This covers file-scoped (`namespace NovaTerminal.Core;`), block (`namespace NovaTerminal.Core`), and sub-namespaces (`.Shortcuts`, `.ThemeImporters`, `.Native`) in one pass because they all share the `namespace NovaTerminal.Core` prefix.

- [ ] **Step 3: Rewrite the App-only sub-namespace *references* (unambiguous — these sub-namespaces exist only in the App)**

```powershell
$appAndTests = Get-ChildItem -Path src/NovaTerminal.App, tests/NovaTerminal.App.Tests -Recurse -Filter *.cs -ErrorAction SilentlyContinue
Replace-InFiles $appAndTests 'NovaTerminal.Core.Shortcuts'     'NovaTerminal.Shell.Shortcuts'
Replace-InFiles $appAndTests 'NovaTerminal.Core.ThemeImporters' 'NovaTerminal.Shell.ThemeImporters'
Replace-InFiles $appAndTests 'NovaTerminal.Core.Native'         'NovaTerminal.Shell.Native'
```

> Safe: the production assembly's only `.Native` namespace is `NovaTerminal.Core.Ssh.Native`, whose token is `NovaTerminal.Core.Ssh.Native` — it does **not** match the literal `NovaTerminal.Core.Native`.

- [ ] **Step 4: Fix the 4 App-local XAML `clr-namespace` references**

```powershell
$axaml = Get-ChildItem -Path src/NovaTerminal.App -Recurse -Filter *.axaml
Replace-InFiles $axaml 'clr-namespace:NovaTerminal.Core"' 'clr-namespace:NovaTerminal.Shell"'
```

> The trailing `"` makes this match only the assembly-local `xmlns:...="clr-namespace:NovaTerminal.Core"` declarations (App.axaml, TerminalPane.axaml, TransferCenter.axaml, ReplayWindow.axaml). It deliberately does **not** match `NewSshConnectionView.axaml`'s `clr-namespace:NovaTerminal.Core.Ssh.Models;assembly=NovaTerminal.Core` (that's the production assembly — handled in Task 2). The `xmlns:core` *alias* name is left unchanged (cosmetic only).

- [ ] **Step 5: Build — let the compiler enumerate the ambiguous consumers**

Run: `scripts/build.ps1 build NovaTerminal.sln`
Expected: **FAIL** with `CS0246`/`CS0234` ("type or namespace not found") errors. These fall into exactly two buckets:

  1. **Consumers of moved types** (files elsewhere in the App / App.Tests that referenced `SessionManager`, `ThemeManager`, `AppPaths`, `Converters`, `TerminalView`, etc. via `using NovaTerminal.Core;` or by same-namespace): add `using NovaTerminal.Shell;` (keep any existing `using NovaTerminal.Core;` — it still resolves the production assembly).
  2. **Moved files needing production-assembly types** (a Shell file that used the SSH stack / input router / path mapper, previously reachable implicitly because it shared the `NovaTerminal.Core` namespace name): add `using NovaTerminal.Core;` to that file.

- [ ] **Step 6: Resolve errors and rebuild until green**

For each error, apply bucket 1 or bucket 2 from Step 5. Re-run `scripts/build.ps1 build NovaTerminal.sln` after each batch. Repeat until:
Expected: build succeeds (0 errors, 0 new warnings).

> Do **not** rename any `using NovaTerminal.Core;` here — bare `NovaTerminal.Core` is still the production assembly until Task 2. Only **add** `using NovaTerminal.Shell;` (bucket 1) or **add** `using NovaTerminal.Core;` (bucket 2).

- [ ] **Step 7: Run the full test suite**

Run: `scripts/build.ps1 test NovaTerminal.sln`
Expected: all tests pass (arch tests included — `Only_the_Core_assembly_uses_NovaTerminal_Core_namespace` still holds, since the production assembly still legitimately owns `NovaTerminal.Core`).

- [ ] **Step 8: Verify no `NovaTerminal.Core` remains in the App folder**

Run: `rtk grep -rn "NovaTerminal.Core" src/NovaTerminal.App/Shell`
Expected: only **bucket-2** `using NovaTerminal.Core;` lines that point at the production assembly (SSH/input/paths). No `namespace NovaTerminal.Core` declarations.

- [ ] **Step 9: Commit**

```powershell
rtk git add -A
rtk git commit -m @'
refactor(app): rename App/Core -> App/Shell, namespace NovaTerminal.Shell (#76)

Removes the App-side half of the three-way "Core" overload. After this the
NovaTerminal.Core namespace is owned solely by the production assembly.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 2: Rename the assembly `NovaTerminal.Core` → `NovaTerminal.Platform` (and `NovaTerminal.Core.Tests` → `NovaTerminal.Platform.Tests`)

With the collision gone, `NovaTerminal.Core` is now unambiguous, so this is a single repo-wide token replace plus folder/csproj/solution renames.

**Files:**
- Move: `src/NovaTerminal.Core/` → `src/NovaTerminal.Platform/`; `tests/NovaTerminal.Core.Tests/` → `tests/NovaTerminal.Platform.Tests/`
- Rename: `NovaTerminal.Core.csproj` → `NovaTerminal.Platform.csproj`; `NovaTerminal.Core.Tests.csproj` → `NovaTerminal.Platform.Tests.csproj`
- Modify (token replace): all `.cs`, `.csproj`, `.axaml` under `src/` and `tests/`, plus `NovaTerminal.sln`

- [ ] **Step 1: Move the production assembly folder + csproj**

```powershell
rtk git mv src/NovaTerminal.Core src/NovaTerminal.Platform
rtk git mv src/NovaTerminal.Platform/NovaTerminal.Core.csproj src/NovaTerminal.Platform/NovaTerminal.Platform.csproj
```

- [ ] **Step 2: Move the test project folder + csproj**

```powershell
rtk git mv tests/NovaTerminal.Core.Tests tests/NovaTerminal.Platform.Tests
rtk git mv tests/NovaTerminal.Platform.Tests/NovaTerminal.Core.Tests.csproj tests/NovaTerminal.Platform.Tests/NovaTerminal.Platform.Tests.csproj
```

> Neither csproj sets `<AssemblyName>` or `<RootNamespace>`, so renaming the `.csproj` filename renames the assembly. No property edits needed.

- [ ] **Step 3: Repo-wide token replace `NovaTerminal.Core` → `NovaTerminal.Platform`**

```powershell
$code = Get-ChildItem -Path src, tests -Recurse -Include *.cs,*.csproj,*.axaml |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
Replace-InFiles $code 'NovaTerminal.Core' 'NovaTerminal.Platform'
```

This correctly turns:
- `namespace NovaTerminal.Core{,.Input,.Execution,.Paths,.Ssh.*}` → `NovaTerminal.Platform{…}` (definitions)
- every `using NovaTerminal.Core;` → `using NovaTerminal.Platform;` (consumers, incl. the bucket-2 usings added in Task 1)
- `NovaTerminal.Core.Tests` → `NovaTerminal.Platform.Tests` (test namespaces + the `InternalsVisibleTo` target)
- ProjectReference paths `..\NovaTerminal.Core\NovaTerminal.Core.csproj` → `..\NovaTerminal.Platform\NovaTerminal.Platform.csproj`
- `NewSshConnectionView.axaml`'s `clr-namespace:NovaTerminal.Core.Ssh.Models;assembly=NovaTerminal.Core` → `...Platform.Ssh.Models;assembly=NovaTerminal.Platform`
- the arch-test string literals + `typeof(global::NovaTerminal.Core.Input...)` in `LayeringTests.cs` (Task 3 rewrites these properly)

> `NovaTerminal.Shell` is a different token and is untouched.

- [ ] **Step 4: Update the solution file**

```powershell
Replace-InFiles (Get-ChildItem -Path NovaTerminal.sln) 'NovaTerminal.Core' 'NovaTerminal.Platform'
```

This rewrites both `NovaTerminal.sln:24` (`NovaTerminal.Core` → `NovaTerminal.Platform`, path `src\NovaTerminal.Core\NovaTerminal.Core.csproj` → `src\NovaTerminal.Platform\NovaTerminal.Platform.csproj`) and `:28` (`NovaTerminal.Core.Tests` → `NovaTerminal.Platform.Tests`, path likewise). Project GUIDs are unchanged.

- [ ] **Step 5: Build**

Run: `scripts/build.ps1 build NovaTerminal.sln`
Expected: build succeeds. If `CS0234`/`CS0246` appear, they are leftover references the replace missed (e.g. a fully-qualified `global::NovaTerminal.Core...` outside the scanned scope) — grep `rtk grep -rn "NovaTerminal.Core" src tests` and fix each, then rebuild.

- [ ] **Step 6: Run the full test suite**

Run: `scripts/build.ps1 test NovaTerminal.sln`
Expected: all tests pass. (The existing `Only_the_Core_assembly_uses_NovaTerminal_Core_namespace` fact still compiles and passes — its body now references `NovaTerminal.Platform`; Task 3 renames/generalizes it.)

- [ ] **Step 7: Commit**

```powershell
rtk git add -A
rtk git commit -m @'
refactor(platform): rename NovaTerminal.Core assembly -> NovaTerminal.Platform (#76)

Assembly + NovaTerminal.Core.Tests -> NovaTerminal.Platform.Tests. Now that the
App side is NovaTerminal.Shell, the NovaTerminal.Core token is unambiguous, so
this is a clean repo-wide rename. No more "Core".

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 3: Generalize the architecture tests (TDD — these are the formal guard)

Replace the single `Only_the_Core_assembly_uses_NovaTerminal_Core_namespace` rule with a Platform alignment fact plus a general "no two assemblies share a namespace prefix" invariant.

**Files:**
- Modify: `tests/NovaTerminal.Architecture.Tests/NamespaceAlignmentTests.cs`
- Verify: `tests/NovaTerminal.Architecture.Tests/LayeringTests.cs` (already fixed by Task 2's replace at line 12)

- [ ] **Step 1: Write the new/failing arch test content**

Replace the entire body of `tests/NovaTerminal.Architecture.Tests/NamespaceAlignmentTests.cs` with:

```csharp
using System.Reflection;
using NetArchTest.Rules;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Each production assembly puts its types in a namespace that matches its assembly name,
/// and no two assemblies share a namespace prefix. The App assembly is the composition
/// root: it owns the bare "NovaTerminal" root plus app-specific buckets (Shell, Controls,
/// Services, Models, ViewModels, Views, UI, CommandAssist) and must not reach into a leaf
/// assembly's reserved prefix.
/// </summary>
public class NamespaceAlignmentTests
{
    private static Assembly LoadByName(string name) => Assembly.Load(name);

    // Leaf assemblies, each owning exactly "NovaTerminal.<Name>.*".
    private static readonly string[] LeafAssemblies =
        { "NovaTerminal.VT", "NovaTerminal.Replay", "NovaTerminal.Rendering",
          "NovaTerminal.Pty", "NovaTerminal.Platform" };

    [Theory]
    [InlineData("NovaTerminal.VT")]
    [InlineData("NovaTerminal.Replay")]
    [InlineData("NovaTerminal.Rendering")]
    [InlineData("NovaTerminal.Pty")]
    [InlineData("NovaTerminal.Platform")]
    public void Leaf_assembly_types_reside_in_its_own_namespace(string asmName)
    {
        var result = Types.InAssembly(LoadByName(asmName))
            .That()
            .DoNotResideInNamespace("System.Runtime.CompilerServices")
            .And().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith(asmName)
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"{asmName} types not in {asmName}.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void No_two_assemblies_share_a_namespace_prefix()
    {
        // Each leaf's reserved prefix must be used by no other assembly (leaf or App).
        var others = new List<string>(LeafAssemblies) { "NovaTerminal.App" };

        foreach (var owner in LeafAssemblies)
        {
            foreach (var other in others)
            {
                if (other == owner) continue;

                var result = Types.InAssembly(LoadByName(other))
                    .That().ArePublic()
                    .Should()
                    .NotResideInNamespaceStartingWith(owner)
                    .GetResult();

                Assert.True(result.IsSuccessful,
                    $"{other} must not use the {owner} namespace prefix. " +
                    $"Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
            }
        }
    }
}
```

> Notes: `NovaTerminal.App` is the assembly name of the UI shell (root namespace `NovaTerminal`, bucket `NovaTerminal.Shell`); `LoadByName("NovaTerminal.App")` loads it. The Architecture.Tests project already references the App assembly transitively via its ProjectReferences (it loads VT/Replay/Rendering/Pty/Platform by name today); if `Assembly.Load("NovaTerminal.App")` throws `FileNotFoundException` at runtime, add `<ProjectReference Include="..\..\src\NovaTerminal.App\NovaTerminal.App.csproj" />` to `tests/NovaTerminal.Architecture.Tests/NovaTerminal.Architecture.Tests.csproj` and re-run.

- [ ] **Step 2: Run the arch tests — verify they pass against the renamed tree**

Run: `scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests/NovaTerminal.Architecture.Tests.csproj`
Expected: PASS — `Leaf_assembly_types_reside_in_its_own_namespace` (5 cases incl. `NovaTerminal.Platform`) and `No_two_assemblies_share_a_namespace_prefix`.

- [ ] **Step 3: Mutation check — prove the new fact actually bites**

Plant a public type in the App assembly under a leaf's reserved prefix; the cross-assembly rule must fail; then remove it and confirm green again.

```powershell
Set-Content -LiteralPath src/NovaTerminal.App/_MutationProbe.cs -Encoding utf8 `
  -Value 'namespace NovaTerminal.Platform.Oops { public class Probe { } }'
scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests/NovaTerminal.Architecture.Tests.csproj
```

Expected: **FAIL** — `No_two_assemblies_share_a_namespace_prefix` reports `NovaTerminal.App must not use the NovaTerminal.Platform namespace prefix. Offenders: NovaTerminal.Platform.Oops.Probe`.

```powershell
Remove-Item -LiteralPath src/NovaTerminal.App/_MutationProbe.cs
scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests/NovaTerminal.Architecture.Tests.csproj
```

Expected: PASS. Confirm no probe remains: `rtk grep -rn "_MutationProbe\|Platform.Oops" src tests` returns nothing.

- [ ] **Step 4: Confirm `LayeringTests.cs` is correct**

Run: `rtk grep -n "NovaTerminal.Platform\|NovaTerminal.Core" tests/NovaTerminal.Architecture.Tests/LayeringTests.cs`
Expected: line 12 reads `typeof(global::NovaTerminal.Platform.Input.TerminalInputSender)`, the layering dependency lists reference `"NovaTerminal.Platform"`, and there are **zero** `NovaTerminal.Core` hits.

- [ ] **Step 5: Commit**

```powershell
rtk git add -A
rtk git commit -m @'
test(arch): enforce no two assemblies share a namespace prefix (#76)

Generalizes the old Core-only rule into per-leaf alignment + a cross-assembly
prefix-exclusivity invariant covering NovaTerminal.Platform and NovaTerminal.Shell.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 4: Update `docs/ARCHITECTURE.md`

**Files:**
- Modify: `docs/ARCHITECTURE.md` (§7, §8, §12, §13, §14, and the dependency diagram ~line 28)

- [ ] **Step 1: Rewrite the dependency diagram and §7 heading/body**

Edit `docs/ARCHITECTURE.md`:
- Line ~28 diagram: `Cli ──► App ──► { Core, VT, Rendering, Pty, Replay }` → `Cli ──► App ──► { Platform, VT, Rendering, Pty, Replay }`
- §7 heading `## 7. Platform / SSH — `NovaTerminal.Core`` → `## 7. Platform / SSH — `NovaTerminal.Platform``
- §7 body: delete the sentence "The name is a historical artifact … renaming `NovaTerminal.Core` itself is a planned follow-up." (it's done now). Replace with: "Renamed from `NovaTerminal.Core` (issue #76) to end the three-way name overload."

- [ ] **Step 2: Update §8 to mention the Shell folder**

In §8 (UI Shell), add to Responsibilities/structure: "Shell composition glue (startup, app paths/logging/services, session & workspace managers, theme manager, command registry, profiles, view host) lives in `src/NovaTerminal.App/Shell/`, namespace `NovaTerminal.Shell` (formerly `App/Core/` + `NovaTerminal.Core`, issue #76)."

- [ ] **Step 3: Update §12 (rule list) and §13 (test table)**

- §12: replace the bullet `- `Only_the_Core_assembly_uses_NovaTerminal_Core_namespace`` with:
  - `- `Leaf_assembly_types_reside_in_its_own_namespace` (VT, Replay, Rendering, Pty, Platform)`
  - `- `No_two_assemblies_share_a_namespace_prefix``
  (Remove the now-redundant per-assembly `All_*_types_use_*` bullets only if you replaced those facts; this plan keeps them, so leave them and just swap the Core bullet.)
- §13: table row `| `NovaTerminal.Core.Tests` | Platform utilities + SSH; …` → `| `NovaTerminal.Platform.Tests` | Platform utilities + SSH; …`

- [ ] **Step 4: Retire the §14 tech-debt entries that are now done**

In §14:
- Delete the bullet starting "**`NovaTerminal.Core` name.**" (resolved by this work).
- Update the "SSH is fragmented across Core and App" bullet: `Core/Ssh/` → `Platform/Ssh/`, and `App/Core/{SftpService,VaultService,SshAskPassCommand}.cs` → `App/Shell/{…}.cs`.
- Update the renderer bullet: `src/NovaTerminal.App/Core/TerminalView.cs` → `src/NovaTerminal.App/Shell/TerminalView.cs` (and `TerminalDrawOperation.cs` likewise).

- [ ] **Step 5: Verify no stale `NovaTerminal.Core` / `App/Core` references remain in the doc**

Run: `rtk grep -n "NovaTerminal.Core\|App/Core" docs/ARCHITECTURE.md`
Expected: zero hits (the only acceptable mentions are historical "(formerly … / renamed from …)" notes you intentionally wrote).

- [ ] **Step 6: Commit**

```powershell
rtk git add docs/ARCHITECTURE.md
rtk git commit -m @'
docs(arch): reflect NovaTerminal.Platform + NovaTerminal.Shell rename (#76)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 5: Final verification sweep

**Files:** none (verification only)

- [ ] **Step 1: Zero residual `NovaTerminal.Core` in tracked source/tests/solution**

Run: `rtk grep -rn "NovaTerminal.Core" src tests NovaTerminal.sln`
Expected: **zero** hits. (Hits under `.claude/worktrees/`, `bin/`, `obj/` are out of scope and excluded — if any appear, confirm they are only in those excluded paths.)

- [ ] **Step 2: No folder or assembly named "Core" remains**

Run: `rtk ls src` then `rtk ls tests`
Expected: `NovaTerminal.Platform` (not `.Core`) under `src/`; `NovaTerminal.Platform.Tests` (not `.Core.Tests`) under `tests/`; no `Core/` under `src/NovaTerminal.App/` (it's `Shell/`).

- [ ] **Step 3: Clean full build + full test**

Run: `scripts/build.ps1 build NovaTerminal.sln`
Expected: 0 errors.
Run: `scripts/build.ps1 test NovaTerminal.sln`
Expected: all suites pass, including `tests/NovaTerminal.Architecture.Tests`.

- [ ] **Step 4: Confirm the issue's acceptance criteria**

- [ ] Exactly zero meanings of "Core" remain (Step 1 + Step 2 prove it).
- [ ] A file path uniquely identifies its assembly (`src/NovaTerminal.Platform/*` vs `src/NovaTerminal.App/Shell/*`).
- [ ] Arch test enforces no cross-assembly namespace sharing (`No_two_assemblies_share_a_namespace_prefix`).

- [ ] **Step 5: Push and open the PR (only if the user asks)**

```powershell
rtk git push -u origin feature/issue-76-rename-core-to-platform
rtk gh pr create --fill --base main
```

> PR body should close #76. Do not push without explicit user confirmation.
```
```
