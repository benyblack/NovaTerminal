# Rename `Ntilde.Core` → `Ntilde.Platform` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the three-way "Core" name overload by renaming the `Ntilde.Core` assembly to `Ntilde.Platform` and the `src/Ntilde.App/Core/` folder to `src/Ntilde.App/Shell/` (namespace `Ntilde.Shell`), and enforce the separation with an architecture test.

**Architecture:** Two-phase rename that keeps the build green at every commit. **Phase 1** renames the App-side folder/namespace first (`Ntilde.Core*` → `Ntilde.Shell*`), which removes the cross-assembly namespace *collision* — after it, the `Ntilde.Core` namespace belongs solely to the production assembly. **Phase 2** is then an unambiguous repo-wide token replace `Ntilde.Core` → `Ntilde.Platform` (assembly + its test project together). The compiler/build is the oracle for the one genuinely non-mechanical step: re-pointing each ambiguous `using Ntilde.Core;` to the right namespace.

**Tech Stack:** .NET 10 / C#, MSBuild, xUnit.v3, NetArchTest.Rules. All builds/tests go through `scripts/build.ps1` (PowerShell wrapper that passes args to `dotnet` with `-nodeReuse:false`).

**Spec:** `docs/plans/2026-05-29-rename-core-to-platform-design.md`

---

## File Structure (what changes)

- `src/Ntilde.App/Core/` → `src/Ntilde.App/Shell/` — ~50 files, namespaces `Ntilde.Core{,.Shortcuts,.ThemeImporters,.Native}` → `Ntilde.Shell{…}`
- `src/Ntilde.Core/` → `src/Ntilde.Platform/` + `Ntilde.Core.csproj` → `Ntilde.Platform.csproj`; namespaces `Ntilde.Core{,.Input,.Execution,.Paths,.Ssh.*}` → `Ntilde.Platform{…}`
- `tests/Ntilde.Core.Tests/` → `tests/Ntilde.Platform.Tests/` + csproj rename
- Consumers: `src/Ntilde.App/**`, `src/Ntilde.Cli/**`, `tests/**` — `using`/`clr-namespace`/ProjectReference fixes
- `Ntilde.sln` — two project entries (paths/names; GUIDs unchanged)
- `tests/Ntilde.Architecture.Tests/{LayeringTests,NamespaceAlignmentTests}.cs` — generalized invariant
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
docs(rename): add design + plan for Ntilde.Core -> Ntilde.Platform (#76)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

- [ ] **Step 3: Establish a green baseline**

Run: `scripts/build.ps1 build Ntilde.sln`
Expected: build succeeds (0 errors). If it fails, stop — the tree was already broken; fix or report before renaming.

---

## Task 1: Rename `App/Core/` → `App/Shell/` (namespace `Ntilde.Shell`)

This phase removes the cross-assembly namespace collision. After it, only the production assembly uses `Ntilde.Core`.

**Files:**
- Move: `src/Ntilde.App/Core/` → `src/Ntilde.App/Shell/` (~50 `.cs`)
- Modify (declarations): every `.cs` under the moved folder
- Modify (App-local sub-namespace references): `src/Ntilde.App/**`, `tests/Ntilde.App.Tests/**`
- Modify (XAML): `src/Ntilde.App/App.axaml`, `Controls/TerminalPane.axaml`, `Controls/TransferCenter.axaml`, `UI/Replay/ReplayWindow.axaml`
- Modify (consumers, compiler-driven): wherever the build flags missing types

- [ ] **Step 1: Move the folder with git**

```powershell
rtk git mv src/Ntilde.App/Core src/Ntilde.App/Shell
```

- [ ] **Step 2: Rewrite namespace declarations in the moved folder**

```powershell
$shell = Get-ChildItem -Path src/Ntilde.App/Shell -Recurse -Filter *.cs
Replace-InFiles $shell 'namespace Ntilde.Core' 'namespace Ntilde.Shell'
```

This covers file-scoped (`namespace Ntilde.Core;`), block (`namespace Ntilde.Core`), and sub-namespaces (`.Shortcuts`, `.ThemeImporters`, `.Native`) in one pass because they all share the `namespace Ntilde.Core` prefix.

- [ ] **Step 3: Rewrite the App-only sub-namespace *references* (unambiguous — these sub-namespaces exist only in the App)**

```powershell
$appAndTests = Get-ChildItem -Path src/Ntilde.App, tests/Ntilde.App.Tests -Recurse -Filter *.cs -ErrorAction SilentlyContinue
Replace-InFiles $appAndTests 'Ntilde.Core.Shortcuts'     'Ntilde.Shell.Shortcuts'
Replace-InFiles $appAndTests 'Ntilde.Core.ThemeImporters' 'Ntilde.Shell.ThemeImporters'
Replace-InFiles $appAndTests 'Ntilde.Core.Native'         'Ntilde.Shell.Native'
```

> Safe: the production assembly's only `.Native` namespace is `Ntilde.Core.Ssh.Native`, whose token is `Ntilde.Core.Ssh.Native` — it does **not** match the literal `Ntilde.Core.Native`.

- [ ] **Step 4: Fix the 4 App-local XAML `clr-namespace` references**

```powershell
$axaml = Get-ChildItem -Path src/Ntilde.App -Recurse -Filter *.axaml
Replace-InFiles $axaml 'clr-namespace:Ntilde.Core"' 'clr-namespace:Ntilde.Shell"'
```

> The trailing `"` makes this match only the assembly-local `xmlns:...="clr-namespace:Ntilde.Core"` declarations (App.axaml, TerminalPane.axaml, TransferCenter.axaml, ReplayWindow.axaml). It deliberately does **not** match `NewSshConnectionView.axaml`'s `clr-namespace:Ntilde.Core.Ssh.Models;assembly=Ntilde.Core` (that's the production assembly — handled in Task 2). The `xmlns:core` *alias* name is left unchanged (cosmetic only).

- [ ] **Step 5: Build — let the compiler enumerate the ambiguous consumers**

Run: `scripts/build.ps1 build Ntilde.sln`
Expected: **FAIL** with `CS0246`/`CS0234` ("type or namespace not found") errors. These fall into exactly two buckets:

  1. **Consumers of moved types** (files elsewhere in the App / App.Tests that referenced `SessionManager`, `ThemeManager`, `AppPaths`, `Converters`, `TerminalView`, etc. via `using Ntilde.Core;` or by same-namespace): add `using Ntilde.Shell;` (keep any existing `using Ntilde.Core;` — it still resolves the production assembly).
  2. **Moved files needing production-assembly types** (a Shell file that used the SSH stack / input router / path mapper, previously reachable implicitly because it shared the `Ntilde.Core` namespace name): add `using Ntilde.Core;` to that file.

- [ ] **Step 6: Resolve errors and rebuild until green**

For each error, apply bucket 1 or bucket 2 from Step 5. Re-run `scripts/build.ps1 build Ntilde.sln` after each batch. Repeat until:
Expected: build succeeds (0 errors, 0 new warnings).

> Do **not** rename any `using Ntilde.Core;` here — bare `Ntilde.Core` is still the production assembly until Task 2. Only **add** `using Ntilde.Shell;` (bucket 1) or **add** `using Ntilde.Core;` (bucket 2).

- [ ] **Step 7: Run the full test suite**

Run: `scripts/build.ps1 test Ntilde.sln`
Expected: all tests pass (arch tests included — `Only_the_Core_assembly_uses_Ntilde_Core_namespace` still holds, since the production assembly still legitimately owns `Ntilde.Core`).

- [ ] **Step 8: Verify no `Ntilde.Core` remains in the App folder**

Run: `rtk grep -rn "Ntilde.Core" src/Ntilde.App/Shell`
Expected: only **bucket-2** `using Ntilde.Core;` lines that point at the production assembly (SSH/input/paths). No `namespace Ntilde.Core` declarations.

- [ ] **Step 9: Commit**

```powershell
rtk git add -A
rtk git commit -m @'
refactor(app): rename App/Core -> App/Shell, namespace Ntilde.Shell (#76)

Removes the App-side half of the three-way "Core" overload. After this the
Ntilde.Core namespace is owned solely by the production assembly.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 2: Rename the assembly `Ntilde.Core` → `Ntilde.Platform` (and `Ntilde.Core.Tests` → `Ntilde.Platform.Tests`)

With the collision gone, `Ntilde.Core` is now unambiguous, so this is a single repo-wide token replace plus folder/csproj/solution renames.

**Files:**
- Move: `src/Ntilde.Core/` → `src/Ntilde.Platform/`; `tests/Ntilde.Core.Tests/` → `tests/Ntilde.Platform.Tests/`
- Rename: `Ntilde.Core.csproj` → `Ntilde.Platform.csproj`; `Ntilde.Core.Tests.csproj` → `Ntilde.Platform.Tests.csproj`
- Modify (token replace): all `.cs`, `.csproj`, `.axaml` under `src/` and `tests/`, plus `Ntilde.sln`

- [ ] **Step 1: Move the production assembly folder + csproj**

```powershell
rtk git mv src/Ntilde.Core src/Ntilde.Platform
rtk git mv src/Ntilde.Platform/Ntilde.Core.csproj src/Ntilde.Platform/Ntilde.Platform.csproj
```

- [ ] **Step 2: Move the test project folder + csproj**

```powershell
rtk git mv tests/Ntilde.Core.Tests tests/Ntilde.Platform.Tests
rtk git mv tests/Ntilde.Platform.Tests/Ntilde.Core.Tests.csproj tests/Ntilde.Platform.Tests/Ntilde.Platform.Tests.csproj
```

> Neither csproj sets `<AssemblyName>` or `<RootNamespace>`, so renaming the `.csproj` filename renames the assembly. No property edits needed.

- [ ] **Step 3: Repo-wide token replace `Ntilde.Core` → `Ntilde.Platform`**

```powershell
$code = Get-ChildItem -Path src, tests -Recurse -Include *.cs,*.csproj,*.axaml |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
Replace-InFiles $code 'Ntilde.Core' 'Ntilde.Platform'
```

This correctly turns:
- `namespace Ntilde.Core{,.Input,.Execution,.Paths,.Ssh.*}` → `Ntilde.Platform{…}` (definitions)
- every `using Ntilde.Core;` → `using Ntilde.Platform;` (consumers, incl. the bucket-2 usings added in Task 1)
- `Ntilde.Core.Tests` → `Ntilde.Platform.Tests` (test namespaces + the `InternalsVisibleTo` target)
- ProjectReference paths `..\Ntilde.Core\Ntilde.Core.csproj` → `..\Ntilde.Platform\Ntilde.Platform.csproj`
- `NewSshConnectionView.axaml`'s `clr-namespace:Ntilde.Core.Ssh.Models;assembly=Ntilde.Core` → `...Platform.Ssh.Models;assembly=Ntilde.Platform`
- the arch-test string literals + `typeof(global::Ntilde.Core.Input...)` in `LayeringTests.cs` (Task 3 rewrites these properly)

> `Ntilde.Shell` is a different token and is untouched.

- [ ] **Step 4: Update the solution file**

```powershell
Replace-InFiles (Get-ChildItem -Path Ntilde.sln) 'Ntilde.Core' 'Ntilde.Platform'
```

This rewrites both `Ntilde.sln:24` (`Ntilde.Core` → `Ntilde.Platform`, path `src\Ntilde.Core\Ntilde.Core.csproj` → `src\Ntilde.Platform\Ntilde.Platform.csproj`) and `:28` (`Ntilde.Core.Tests` → `Ntilde.Platform.Tests`, path likewise). Project GUIDs are unchanged.

- [ ] **Step 5: Build**

Run: `scripts/build.ps1 build Ntilde.sln`
Expected: build succeeds. If `CS0234`/`CS0246` appear, they are leftover references the replace missed (e.g. a fully-qualified `global::Ntilde.Core...` outside the scanned scope) — grep `rtk grep -rn "Ntilde.Core" src tests` and fix each, then rebuild.

- [ ] **Step 6: Run the full test suite**

Run: `scripts/build.ps1 test Ntilde.sln`
Expected: all tests pass. (The existing `Only_the_Core_assembly_uses_Ntilde_Core_namespace` fact still compiles and passes — its body now references `Ntilde.Platform`; Task 3 renames/generalizes it.)

- [ ] **Step 7: Commit**

```powershell
rtk git add -A
rtk git commit -m @'
refactor(platform): rename Ntilde.Core assembly -> Ntilde.Platform (#76)

Assembly + Ntilde.Core.Tests -> Ntilde.Platform.Tests. Now that the
App side is Ntilde.Shell, the Ntilde.Core token is unambiguous, so
this is a clean repo-wide rename. No more "Core".

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 3: Generalize the architecture tests (TDD — these are the formal guard)

Replace the single `Only_the_Core_assembly_uses_Ntilde_Core_namespace` rule with a Platform alignment fact plus a general "no two assemblies share a namespace prefix" invariant.

**Files:**
- Modify: `tests/Ntilde.Architecture.Tests/NamespaceAlignmentTests.cs`
- Verify: `tests/Ntilde.Architecture.Tests/LayeringTests.cs` (already fixed by Task 2's replace at line 12)

- [ ] **Step 1: Write the new/failing arch test content**

Replace the entire body of `tests/Ntilde.Architecture.Tests/NamespaceAlignmentTests.cs` with:

```csharp
using System.Reflection;
using NetArchTest.Rules;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Each production assembly puts its types in a namespace that matches its assembly name,
/// and no two assemblies share a namespace prefix. The App assembly is the composition
/// root: it owns the bare "Ntilde" root plus app-specific buckets (Shell, Controls,
/// Services, Models, ViewModels, Views, UI, CommandAssist) and must not reach into a leaf
/// assembly's reserved prefix.
/// </summary>
public class NamespaceAlignmentTests
{
    private static Assembly LoadByName(string name) => Assembly.Load(name);

    // Leaf assemblies, each owning exactly "Ntilde.<Name>.*".
    private static readonly string[] LeafAssemblies =
        { "Ntilde.VT", "Ntilde.Replay", "Ntilde.Rendering",
          "Ntilde.Pty", "Ntilde.Platform" };

    [Theory]
    [InlineData("Ntilde.VT")]
    [InlineData("Ntilde.Replay")]
    [InlineData("Ntilde.Rendering")]
    [InlineData("Ntilde.Pty")]
    [InlineData("Ntilde.Platform")]
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
        var others = new List<string>(LeafAssemblies) { "Ntilde.App" };

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

> Notes: `Ntilde.App` is the assembly name of the UI shell (root namespace `Ntilde`, bucket `Ntilde.Shell`); `LoadByName("Ntilde.App")` loads it. The Architecture.Tests project already references the App assembly transitively via its ProjectReferences (it loads VT/Replay/Rendering/Pty/Platform by name today); if `Assembly.Load("Ntilde.App")` throws `FileNotFoundException` at runtime, add `<ProjectReference Include="..\..\src\Ntilde.App\Ntilde.App.csproj" />` to `tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj` and re-run.

- [ ] **Step 2: Run the arch tests — verify they pass against the renamed tree**

Run: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj`
Expected: PASS — `Leaf_assembly_types_reside_in_its_own_namespace` (5 cases incl. `Ntilde.Platform`) and `No_two_assemblies_share_a_namespace_prefix`.

- [ ] **Step 3: Mutation check — prove the new fact actually bites**

Plant a public type in the App assembly under a leaf's reserved prefix; the cross-assembly rule must fail; then remove it and confirm green again.

```powershell
Set-Content -LiteralPath src/Ntilde.App/_MutationProbe.cs -Encoding utf8 `
  -Value 'namespace Ntilde.Platform.Oops { public class Probe { } }'
scripts/build.ps1 test tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj
```

Expected: **FAIL** — `No_two_assemblies_share_a_namespace_prefix` reports `Ntilde.App must not use the Ntilde.Platform namespace prefix. Offenders: Ntilde.Platform.Oops.Probe`.

```powershell
Remove-Item -LiteralPath src/Ntilde.App/_MutationProbe.cs
scripts/build.ps1 test tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj
```

Expected: PASS. Confirm no probe remains: `rtk grep -rn "_MutationProbe\|Platform.Oops" src tests` returns nothing.

- [ ] **Step 4: Confirm `LayeringTests.cs` is correct**

Run: `rtk grep -n "Ntilde.Platform\|Ntilde.Core" tests/Ntilde.Architecture.Tests/LayeringTests.cs`
Expected: line 12 reads `typeof(global::Ntilde.Platform.Input.TerminalInputSender)`, the layering dependency lists reference `"Ntilde.Platform"`, and there are **zero** `Ntilde.Core` hits.

- [ ] **Step 5: Commit**

```powershell
rtk git add -A
rtk git commit -m @'
test(arch): enforce no two assemblies share a namespace prefix (#76)

Generalizes the old Core-only rule into per-leaf alignment + a cross-assembly
prefix-exclusivity invariant covering Ntilde.Platform and Ntilde.Shell.

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
- §7 heading `## 7. Platform / SSH — `Ntilde.Core`` → `## 7. Platform / SSH — `Ntilde.Platform``
- §7 body: delete the sentence "The name is a historical artifact … renaming `Ntilde.Core` itself is a planned follow-up." (it's done now). Replace with: "Renamed from `Ntilde.Core` (issue #76) to end the three-way name overload."

- [ ] **Step 2: Update §8 to mention the Shell folder**

In §8 (UI Shell), add to Responsibilities/structure: "Shell composition glue (startup, app paths/logging/services, session & workspace managers, theme manager, command registry, profiles, view host) lives in `src/Ntilde.App/Shell/`, namespace `Ntilde.Shell` (formerly `App/Core/` + `Ntilde.Core`, issue #76)."

- [ ] **Step 3: Update §12 (rule list) and §13 (test table)**

- §12: replace the bullet `- `Only_the_Core_assembly_uses_Ntilde_Core_namespace`` with:
  - `- `Leaf_assembly_types_reside_in_its_own_namespace` (VT, Replay, Rendering, Pty, Platform)`
  - `- `No_two_assemblies_share_a_namespace_prefix``
  (Remove the now-redundant per-assembly `All_*_types_use_*` bullets only if you replaced those facts; this plan keeps them, so leave them and just swap the Core bullet.)
- §13: table row `| `Ntilde.Core.Tests` | Platform utilities + SSH; …` → `| `Ntilde.Platform.Tests` | Platform utilities + SSH; …`

- [ ] **Step 4: Retire the §14 tech-debt entries that are now done**

In §14:
- Delete the bullet starting "**`Ntilde.Core` name.**" (resolved by this work).
- Update the "SSH is fragmented across Core and App" bullet: `Core/Ssh/` → `Platform/Ssh/`, and `App/Core/{SftpService,VaultService,SshAskPassCommand}.cs` → `App/Shell/{…}.cs`.
- Update the renderer bullet: `src/Ntilde.App/Core/TerminalView.cs` → `src/Ntilde.App/Shell/TerminalView.cs` (and `TerminalDrawOperation.cs` likewise).

- [ ] **Step 5: Verify no stale `Ntilde.Core` / `App/Core` references remain in the doc**

Run: `rtk grep -n "Ntilde.Core\|App/Core" docs/ARCHITECTURE.md`
Expected: zero hits (the only acceptable mentions are historical "(formerly … / renamed from …)" notes you intentionally wrote).

- [ ] **Step 6: Commit**

```powershell
rtk git add docs/ARCHITECTURE.md
rtk git commit -m @'
docs(arch): reflect Ntilde.Platform + Ntilde.Shell rename (#76)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

## Task 5: Final verification sweep

**Files:** none (verification only)

- [ ] **Step 1: Zero residual `Ntilde.Core` in tracked source/tests/solution**

Run: `rtk grep -rn "Ntilde.Core" src tests Ntilde.sln`
Expected: **zero** hits. (Hits under `.claude/worktrees/`, `bin/`, `obj/` are out of scope and excluded — if any appear, confirm they are only in those excluded paths.)

- [ ] **Step 2: No folder or assembly named "Core" remains**

Run: `rtk ls src` then `rtk ls tests`
Expected: `Ntilde.Platform` (not `.Core`) under `src/`; `Ntilde.Platform.Tests` (not `.Core.Tests`) under `tests/`; no `Core/` under `src/Ntilde.App/` (it's `Shell/`).

- [ ] **Step 3: Clean full build + full test**

Run: `scripts/build.ps1 build Ntilde.sln`
Expected: 0 errors.
Run: `scripts/build.ps1 test Ntilde.sln`
Expected: all suites pass, including `tests/Ntilde.Architecture.Tests`.

- [ ] **Step 4: Confirm the issue's acceptance criteria**

- [ ] Exactly zero meanings of "Core" remain (Step 1 + Step 2 prove it).
- [ ] A file path uniquely identifies its assembly (`src/Ntilde.Platform/*` vs `src/Ntilde.App/Shell/*`).
- [ ] Arch test enforces no cross-assembly namespace sharing (`No_two_assemblies_share_a_namespace_prefix`).

- [ ] **Step 5: Push and open the PR (only if the user asks)**

```powershell
rtk git push -u origin feature/issue-76-rename-core-to-platform
rtk gh pr create --fill --base main
```

> PR body should close #76. Do not push without explicit user confirmation.
```
```
