# Ntilde Architecture Foundation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the structural risk that drove the 2026-05-28 architecture review — assembly/namespace collapse, missing layering enforcement, sln drift, test-project fragmentation — so that subsequent module-extraction work cannot silently re-violate boundaries.

**Architecture:** Bottom-up consolidation. First lock in invariants (central package management, warnings-as-errors, NetArchTest, sln membership). Then align namespaces to assemblies. Then unify the test stack and add the missing VT/Rendering test projects. Then rewrite the architecture docs to match reality. Then split the `ITerminalSession` god-interface. Bigger module extractions (Renderer composition, SSH, CommandAssist, Bootstrap) are deferred to dedicated follow-up plans — they cannot be safely done without the guardrails this plan installs.

**Tech Stack:** .NET 10, C# 13, MSBuild SDK-style projects, xUnit v3, [NetArchTest.Rules](https://github.com/BenMorris/NetArchTest), Directory.Packages.props (Central Package Management).

**Prerequisites:** Branch off `main`. All work goes through normal review. Tests must pass after every commit. Use `scripts/build.ps1` / `scripts/build.sh` — never raw `dotnet build` (per `CLAUDE.md`).

**Source of findings:** `docs/plans/2026-05-28-architecture-module-boundaries-review.md`. Sections referenced as `[A]`, `[B]`, etc. below.

---

## Priority Rationale

The findings break into ten work items. The order below is chosen so that **each phase locks in a guarantee the next phase relies on**:

| Phase | What | Why this order | Addresses |
|---|---|---|---|
| 1 | Central Package Management + warnings hardening + sln cleanup | Cheapest, prevents version drift, must precede any rename so we have one place to bump xunit/skia/etc. | F.1, F.5, F.6, H |
| 2 | Architecture-test scaffold (NetArchTest) | Captures *today's* layering violations as known-failure tests; every subsequent phase shrinks the violation count. Without this, renames silently regress. | A, B.3, C.2, "Most concentrated risk" |
| 3 | Namespace alignment (VT → Replay → Rendering → Pty) | Touches every file via `using`. Must happen before module extraction (otherwise extractions move things around with the wrong namespace and the rename gets harder, not easier). VT is the leaf, so go bottom-up. | B, C.1 |
| 4 | Test stack unification + missing VT/Rendering test projects | Need a single test framework before extracting; need VT tests in isolation before splitting `TerminalBuffer`. | F.2, F.3, F.4, F.7 |
| 5 | `ITerminalSession` interface split | Has to happen before extracting SSH/Renderer because both consumers want a narrower contract. Best done after architecture tests so the new layering is enforced. | D, C.4 |
| 6 | Documentation realignment | After phases 1–5, the docs need a single comprehensive rewrite (multiple smaller rewrites would waste effort). | A |
| 7+ | Follow-up plans (deferred) | Each is multi-day work that needs its own detailed plan. Scope statements provided at the end of this document. | E, G, I, J |

Phases 1–5 are the foundation. They are detailed below with full task-level steps. Phase 6 is a single doc-rewrite task. Phases 7+ get scope statements only.

---

## Phase 1 — Foundation Guardrails

**Outcome:** One place to manage package versions, one place to set common project properties, all projects in the sln, no analyzer-blind builds.

### Task 1.1 — Add Central Package Management (CPM)

**Files:**
- Create: `Directory.Packages.props`
- Modify: every `*.csproj` under `src/` and `tests/`

- [ ] **Step 1: Create `Directory.Packages.props` at the repo root**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>

  <ItemGroup>
    <!-- UI -->
    <PackageVersion Include="Avalonia" Version="12.0.3" />
    <PackageVersion Include="Avalonia.Desktop" Version="12.0.3" />
    <PackageVersion Include="Avalonia.Themes.Fluent" Version="12.0.3" />
    <PackageVersion Include="Avalonia.Fonts.Inter" Version="12.0.3" />
    <PackageVersion Include="AvaloniaUI.DiagnosticsSupport" Version="2.2.0" />
    <PackageVersion Include="Avalonia.Headless.XUnit" Version="12.0.3" />

    <!-- Rendering. NOTE: today Rendering.csproj uses 3.116.1 and App.csproj uses 3.119.4-preview.
         Pin both to a single version here. Use 3.119.4-preview.1.1 (the newer one) initially;
         if Rendering breaks, downgrade to 3.116.1 in a follow-up and document the reason. -->
    <PackageVersion Include="SkiaSharp" Version="3.119.4-preview.1.1" />
    <PackageVersion Include="SkiaSharp.HarfBuzz" Version="3.119.4-preview.1.1" />
    <PackageVersion Include="SkiaSharp.NativeAssets.Win32" Version="3.119.4-preview.1.1" />
    <PackageVersion Include="SkiaSharp.NativeAssets.Linux" Version="3.119.4-preview.1.1" />

    <!-- Crypto -->
    <PackageVersion Include="System.Security.Cryptography.ProtectedData" Version="10.0.2" />

    <!-- Test stack — unified on xunit v3. Old xunit 2.9.3 pin is intentionally removed. -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.4" />
    <PackageVersion Include="Moq" Version="4.20.72" />
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />

    <!-- Architecture testing (added in Phase 2 but reserved here for one-shot CPM) -->
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Strip `Version=` from every `PackageReference` in every csproj**

Run this PowerShell from the repo root to find offenders:

```powershell
Get-ChildItem -Recurse -Include *.csproj | Select-String -Pattern 'PackageReference.*Version='
```

Then for each csproj listed, edit each `<PackageReference Include="X" Version="Y" />` to `<PackageReference Include="X" />`. Preserve `<IncludeAssets>` / `<PrivateAssets>` child elements.

Affected files (verified during review):
- `src/Ntilde.App/Ntilde.App.csproj` — Avalonia*, AvaloniaUI.DiagnosticsSupport, SkiaSharp*, System.Security.Cryptography.ProtectedData
- `src/Ntilde.Rendering/Ntilde.Rendering.csproj` — SkiaSharp, SkiaSharp.HarfBuzz
- `tests/Ntilde.Tests/Ntilde.Tests.csproj` — Avalonia.Headless.XUnit, coverlet.collector, Microsoft.NET.Test.Sdk, Moq, xunit.v3, xunit.runner.visualstudio
- `tests/Ntilde.Core.Tests/Ntilde.Core.Tests.csproj` — same set but currently pinned to xunit 2.9.3
- `tests/Ntilde.Benchmarks/Ntilde.Benchmarks.csproj` — inspect and update
- `tests/Ntilde.ExternalSuites/Ntilde.ExternalSuites.csproj` — inspect and update

- [ ] **Step 3: Change `Ntilde.Core.Tests` to use xunit.v3**

Edit `tests/Ntilde.Core.Tests/Ntilde.Core.Tests.csproj`. Replace:

```xml
<PackageReference Include="xunit" />
```

with:

```xml
<PackageReference Include="xunit.v3" />
```

If any test file imports `Xunit.Sdk` or uses `[Theory(Skip=...)]` quirks that changed in v3, fix them now. Run:

```powershell
scripts/build.ps1 build tests/Ntilde.Core.Tests
```

Expected: build succeeds. If it doesn't, the compile errors point at the v2→v3 API drift. Common fixes documented at https://xunit.net/docs/getting-started/v3/migration.

- [ ] **Step 4: Restore and build everything**

```powershell
scripts/build.ps1 restore
scripts/build.ps1 build
```

Expected: no `NU1605` (downgrade) or `NU1008` (centrally-managed-but-version-set) errors.

- [ ] **Step 5: Run full test suite to verify nothing regressed**

```powershell
scripts/build.ps1 test
```

Expected: same pass/fail count as before this task. Investigate any new failure before committing.

- [ ] **Step 6: Commit**

```powershell
git add Directory.Packages.props src/**/*.csproj tests/**/*.csproj
git commit -m "build: adopt Central Package Management and unify on xunit.v3"
```

### Task 1.2 — Harden `Directory.Build.props`

**Files:**
- Modify: `Directory.Build.props`

- [ ] **Step 1: Replace the current minimal Directory.Build.props**

Current contents are just version + `UseSharedCompilation=false`. Replace the file with:

```xml
<Project>
  <PropertyGroup>
    <Version>0.2.0</Version>
    <AssemblyVersion>0.2.0.0</AssemblyVersion>
    <FileVersion>0.2.0.0</FileVersion>
    <InformationalVersion>0.2.0</InformationalVersion>
  </PropertyGroup>

  <PropertyGroup>
    <!-- See CLAUDE.md: the Roslyn build server holds parent pipe handles after exit,
         causing test/CI/Claude-Code Bash captures to hang. Per-project setting only;
         the *outer* dotnet invocation also needs -nodeReuse:false (encoded in
         scripts/build.{ps1,sh}). -->
    <UseSharedCompilation>false</UseSharedCompilation>
  </PropertyGroup>

  <PropertyGroup>
    <!-- Centralized language settings. Individual csproj files no longer need
         to repeat Nullable/ImplicitUsings. -->
    <TargetFramework Condition="'$(TargetFramework)' == ''">net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
  </PropertyGroup>

  <PropertyGroup Condition="'$(IsTestProject)' == 'true' OR $(MSBuildProjectName.EndsWith('.Tests'))">
    <!-- Tests are allowed to have some leeway. Keep WarningsAsErrors but turn off
         the most noise-prone analyzer rules for test code. -->
    <NoWarn>$(NoWarn);xUnit1031;CA1707;CA2007</NoWarn>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Remove the now-redundant properties from each csproj**

For every csproj, delete these lines (they're now inherited):
- `<TargetFramework>net10.0</TargetFramework>` (keep only if the project sets a non-default like `net10.0-windows`)
- `<Nullable>enable</Nullable>`
- `<ImplicitUsings>enable</ImplicitUsings>`

- [ ] **Step 3: Build and triage any new warnings-as-errors failures**

```powershell
scripts/build.ps1 build
```

This will almost certainly surface warnings the project was tolerating. **Do not blanket-suppress them.** Triage:

1. If the warning is a real bug (`CS8602` null deref, `CA1816` Dispose pattern), fix it.
2. If the rule is genuinely noisy for this codebase, add a single targeted `<NoWarn>` entry to `Directory.Build.props` with a comment explaining why.
3. If there are >50 violations of one rule, document the count in this task's commit message and disable the rule with `NoWarn` for now; create a follow-up issue.

Expected: build green. If you cannot get green in <2 hours of fixes, downgrade `TreatWarningsAsErrors` to `false` for this commit only, file a follow-up ticket titled "Re-enable TreatWarningsAsErrors after analyzer triage," and proceed. **Do not skip this phase entirely** — the rest of the plan assumes centralized props.

- [ ] **Step 4: Run tests**

```powershell
scripts/build.ps1 test
```

Expected: same pass/fail as before.

- [ ] **Step 5: Commit**

```powershell
git add Directory.Build.props src/**/*.csproj tests/**/*.csproj
git commit -m "build: centralize project properties and enable warnings-as-errors"
```

### Task 1.3 — Add missing projects to the solution

**Files:**
- Modify: `Ntilde.sln`

- [ ] **Step 1: Add `Ntilde.Conformance` to the solution**

```powershell
dotnet sln Ntilde.sln add src/Ntilde.Conformance/Ntilde.Conformance.csproj
```

- [ ] **Step 2: Add `Ntilde.ExternalSuites` to the solution**

```powershell
dotnet sln Ntilde.sln add tests/Ntilde.ExternalSuites/Ntilde.ExternalSuites.csproj
```

- [ ] **Step 3: Verify**

```powershell
dotnet sln Ntilde.sln list
```

Expected output includes both `Ntilde.Conformance` and `Ntilde.ExternalSuites`.

- [ ] **Step 4: Build the whole solution to confirm**

```powershell
scripts/build.ps1 build Ntilde.sln
```

Expected: every project (now 12 total) builds.

- [ ] **Step 5: Commit**

```powershell
git add Ntilde.sln
git commit -m "build: add Conformance and ExternalSuites projects to solution"
```

---

## Phase 2 — Architecture-Test Scaffold

**Outcome:** A `Ntilde.Architecture.Tests` project that encodes layering rules. Tests start out failing (capturing today's known violations as `[Fact(Skip="...")]` with the current violation count). Each subsequent phase un-skips one rule.

This is the single most leverage-dense phase. After it lands, no PR can re-introduce the violations being fixed by phases 3–5.

### Task 2.1 — Create the architecture-tests project

**Files:**
- Create: `tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj`
- Create: `tests/Ntilde.Architecture.Tests/LayeringTests.cs`
- Create: `tests/Ntilde.Architecture.Tests/NamespaceAlignmentTests.cs`
- Modify: `Ntilde.sln`

- [ ] **Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="NetArchTest.Rules" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <!-- Reference every production assembly so NetArchTest can load them. -->
    <ProjectReference Include="..\..\src\Ntilde.VT\Ntilde.VT.csproj" />
    <ProjectReference Include="..\..\src\Ntilde.Replay\Ntilde.Replay.csproj" />
    <ProjectReference Include="..\..\src\Ntilde.Rendering\Ntilde.Rendering.csproj" />
    <ProjectReference Include="..\..\src\Ntilde.Pty\Ntilde.Pty.csproj" />
    <ProjectReference Include="..\..\src\Ntilde.Core\Ntilde.Core.csproj" />
    <ProjectReference Include="..\..\src\Ntilde.App\Ntilde.App.csproj" />
    <ProjectReference Include="..\..\src\Ntilde.Cli\Ntilde.Cli.csproj" />
    <ProjectReference Include="..\..\src\Ntilde.Conformance\Ntilde.Conformance.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create `LayeringTests.cs` (assembly dependency rules)**

```csharp
using System.Reflection;
using NetArchTest.Rules;

namespace Ntilde.Architecture.Tests;

public class LayeringTests
{
    private static Assembly Vt        => typeof(global::Ntilde.Core.AnsiParser).Assembly; // namespace fixed in Phase 3
    private static Assembly Replay    => typeof(global::Ntilde.Core.Replay.ReplayReader).Assembly;
    private static Assembly Rendering => typeof(global::Ntilde.Core.GlyphAtlas).Assembly;
    private static Assembly Pty       => typeof(global::Ntilde.Core.ITerminalSession).Assembly;
    private static Assembly Core      => typeof(global::Ntilde.Core.Input.TerminalInputSender).Assembly;

    [Fact]
    public void Vt_must_be_a_leaf_assembly()
    {
        var result = Types.InAssembly(Vt)
            .Should()
            .NotHaveDependencyOnAny(
                "Ntilde.Replay",
                "Ntilde.Rendering",
                "Ntilde.Pty",
                "Ntilde.Core",
                "Ntilde.App",
                "Avalonia",
                "SkiaSharp")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"VT must not depend on higher layers. Offenders: {Join(result.FailingTypeNames)}");
    }

    [Fact]
    public void Rendering_only_depends_on_Vt_and_Skia()
    {
        var result = Types.InAssembly(Rendering)
            .Should()
            .NotHaveDependencyOnAny(
                "Ntilde.Replay",
                "Ntilde.Pty",
                "Ntilde.Core",
                "Ntilde.App",
                "Avalonia")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Rendering may only reference VT + Skia. Offenders: {Join(result.FailingTypeNames)}");
    }

    [Fact]
    public void Replay_only_depends_on_Vt()
    {
        var result = Types.InAssembly(Replay)
            .Should()
            .NotHaveDependencyOnAny(
                "Ntilde.Rendering",
                "Ntilde.Pty",
                "Ntilde.Core",
                "Ntilde.App",
                "Avalonia",
                "SkiaSharp")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Replay may only reference VT. Offenders: {Join(result.FailingTypeNames)}");
    }

    // KNOWN VIOLATION (Section C.2 of the architecture review).
    // Today, Pty references VT (for `ITerminalSession.AttachBuffer(TerminalBuffer)`).
    // The fix is Phase 5 (ITerminalSession decomposition). Un-skip then.
    [Fact(Skip = "Known violation — fixed in Phase 5 of architecture-foundation-plan")]
    public void Pty_must_not_depend_on_Vt()
    {
        var result = Types.InAssembly(Pty)
            .Should()
            .NotHaveDependencyOn("Ntilde.VT")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Pty must not reference VT. Offenders: {Join(result.FailingTypeNames)}");
    }

    [Fact]
    public void No_production_assembly_references_test_assemblies()
    {
        foreach (var asm in new[] { Vt, Replay, Rendering, Pty, Core })
        {
            var result = Types.InAssembly(asm)
                .Should()
                .NotHaveDependencyOnAny("xunit", "xunit.v3", "Moq", "NetArchTest.Rules")
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"{asm.GetName().Name} must not reference test infrastructure. " +
                $"Offenders: {Join(result.FailingTypeNames)}");
        }
    }

    private static string Join(IEnumerable<string>? names)
        => names is null ? "(none)" : string.Join(", ", names);
}
```

- [ ] **Step 3: Create `NamespaceAlignmentTests.cs`**

This file captures the Section B finding. **Every test starts skipped** with the current violation count. They get un-skipped one at a time in Phase 3.

```csharp
using System.Reflection;
using NetArchTest.Rules;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Each production assembly should put its types in a namespace that matches its assembly name.
/// Today, 5 of 6 production assemblies all use "Ntilde.Core" as their root namespace.
/// Phase 3 fixes this one assembly at a time.
/// </summary>
public class NamespaceAlignmentTests
{
    private static Assembly LoadByName(string name)
        => AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == name);

    [Fact(Skip = "Known violation — fixed in Phase 3 (VT subphase)")]
    public void All_VT_types_use_Ntilde_VT_namespace()
    {
        var result = Types.InAssembly(LoadByName("Ntilde.VT"))
            .That()
            .DoNotResideInNamespace("System.Runtime.CompilerServices")     // attributes the compiler generates
            .And().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith("Ntilde.VT")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"VT types not in Ntilde.VT.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact(Skip = "Known violation — fixed in Phase 3 (Replay subphase)")]
    public void All_Replay_types_use_Ntilde_Replay_namespace()
    {
        var result = Types.InAssembly(LoadByName("Ntilde.Replay"))
            .That().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith("Ntilde.Replay")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Replay types not in Ntilde.Replay.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact(Skip = "Known violation — fixed in Phase 3 (Rendering subphase)")]
    public void All_Rendering_types_use_Ntilde_Rendering_namespace()
    {
        var result = Types.InAssembly(LoadByName("Ntilde.Rendering"))
            .That().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith("Ntilde.Rendering")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Rendering types not in Ntilde.Rendering.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact(Skip = "Known violation — fixed in Phase 3 (Pty subphase)")]
    public void All_Pty_types_use_Ntilde_Pty_namespace()
    {
        var result = Types.InAssembly(LoadByName("Ntilde.Pty"))
            .That().ArePublic()
            .Should()
            .ResideInNamespaceStartingWith("Ntilde.Pty")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Pty types not in Ntilde.Pty.*: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
```

- [ ] **Step 4: Add the project to the sln**

```powershell
dotnet sln Ntilde.sln add tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj
```

- [ ] **Step 5: Build and run only the architecture tests**

```powershell
scripts/build.ps1 build tests/Ntilde.Architecture.Tests
scripts/build.ps1 test --filter "FullyQualifiedName~Architecture.Tests"
```

Expected: the un-skipped layering tests pass (`Vt_must_be_a_leaf_assembly`, `Rendering_only_depends_on_Vt_and_Skia`, `Replay_only_depends_on_Vt`, `No_production_assembly_references_test_assemblies`); the skipped tests are reported as skipped, not failing.

If any of the un-skipped tests *fail*, the current assembly graph is even worse than the review claimed. Inspect the violation list, document in the commit message, and either fix immediately or add a Skip with a known-issue note.

- [ ] **Step 6: Commit**

```powershell
git add tests/Ntilde.Architecture.Tests/ Ntilde.sln
git commit -m "test: add NetArchTest scaffold with current violations captured as known issues"
```

---

## Phase 3 — Namespace Alignment

**Outcome:** Each production assembly's types live in a namespace that matches its assembly name. Sub-phases bottom-up so dependents stay green at every step.

**Procedure for every sub-phase:**

1. Enumerate the namespace declarations in the target assembly (`grep -rn '^namespace ' src/Ntilde.<X>`).
2. Map old → new (`Ntilde.Core` → `Ntilde.<X>`, sub-namespaces preserved).
3. Do a project-scoped find-replace on `namespace ` declarations and any explicit cross-references (`using Ntilde.Core.<Sub>;` referring to the renamed sub-namespace).
4. In every *consumer* project, add `using Ntilde.<X>;` (or fully qualify) where compile errors appear.
5. Build solution.
6. Run all tests.
7. Un-skip the matching `NamespaceAlignment` architecture test (remove the `Skip` argument).
8. Run architecture tests, verify it now passes.
9. Commit.

The find-replace is mechanical but huge. Use one IDE refactor per sub-phase to keep the diff reviewable.

### Task 3.1 — Rename VT namespace (`Ntilde.Core` → `Ntilde.VT`)

**Files:**
- Modify: every `*.cs` under `src/Ntilde.VT/`
- Modify: every `*.cs` consuming VT types (across `Replay`, `Rendering`, `Pty`, `Core`, `App`, `Cli`, tests)
- Modify: `tests/Ntilde.Architecture.Tests/NamespaceAlignmentTests.cs` (un-skip the VT test)

- [ ] **Step 1: List current VT namespaces**

```powershell
Get-ChildItem -Recurse -Path src/Ntilde.VT -Filter *.cs `
  | Select-String -Pattern '^namespace ' `
  | Select-Object -ExpandProperty Line `
  | Sort-Object -Unique
```

Expected output (from the review):

```
namespace Ntilde.Core
namespace Ntilde.Core.Export
namespace Ntilde.Core.Replay
namespace Ntilde.Core.Storage
namespace Ntilde.Core;
```

Mapping:

| Old | New |
|---|---|
| `Ntilde.Core` | `Ntilde.VT` |
| `Ntilde.Core.Export` | `Ntilde.VT.Export` |
| `Ntilde.Core.Storage` | `Ntilde.VT.Storage` |
| `Ntilde.Core.Replay` (when *defined* in VT) | **stays as `Ntilde.Core.Replay`** — see step 2 |

- [ ] **Step 2: Handle the `Ntilde.Core.Replay` ambiguity first**

Both the VT project and the Replay project declare types in `Ntilde.Core.Replay`. **Find which types are in which.**

```powershell
Get-ChildItem -Recurse -Path src/Ntilde.VT -Filter *.cs | `
  Select-String -Pattern 'namespace Ntilde\.Core\.Replay' -List | `
  Select-Object Path
Get-ChildItem -Recurse -Path src/Ntilde.Replay -Filter *.cs | `
  Select-String -Pattern 'namespace Ntilde\.Core\.Replay' -List | `
  Select-Object Path
```

Inspect each file. Types that *describe* a replay (snapshot models, replay-formatted data) belong in the **Replay** namespace. Types that are general buffer state happen to be used during replay belong in **VT**.

In VT: rename the namespace of any `Ntilde.Core.Replay`-namespaced file to `Ntilde.VT` (if it's general-purpose) or `Ntilde.VT.Replay` (if it's specifically replay-shaped data owned by VT). Document the call in the commit message.

In Replay (Task 3.2 territory, but flagged now to plan): files there will become `Ntilde.Replay`.

- [ ] **Step 3: Rename namespaces in VT files**

For every `.cs` under `src/Ntilde.VT/`:
- `namespace Ntilde.Core` → `namespace Ntilde.VT`
- `namespace Ntilde.Core;` → `namespace Ntilde.VT;`
- `namespace Ntilde.Core.Export` → `namespace Ntilde.VT.Export`
- `namespace Ntilde.Core.Storage` → `namespace Ntilde.VT.Storage`
- `namespace Ntilde.Core.Replay` (in VT) → `namespace Ntilde.VT` or `namespace Ntilde.VT.Replay` per step 2

A safe approach: use the IDE's "rename namespace" refactor, file by file. Avoid global sed — it will catch namespace strings inside string literals (replay format strings, log messages) and break them.

- [ ] **Step 4: Build the VT project alone**

```powershell
scripts/build.ps1 build src/Ntilde.VT
```

Expected: green. Errors here are internal to VT (one file referenced another via the old namespace).

- [ ] **Step 5: Build the rest of the solution and fix consumer errors**

```powershell
scripts/build.ps1 build Ntilde.sln
```

Expected: many errors of the form `The type or namespace 'TerminalBuffer' could not be found`. For each file flagged, add `using Ntilde.VT;` (and `using Ntilde.VT.Export;` / `.Storage;` as appropriate).

Do **not** remove existing `using Ntilde.Core;` lines yet — other assemblies still use that namespace until later sub-phases.

- [ ] **Step 6: Run all tests**

```powershell
scripts/build.ps1 test
```

Expected: same pass/fail as before. Any regression here is a namespace mistake — a type collision between same-named types now in different namespaces. Disambiguate at the call site with a fully qualified name.

- [ ] **Step 7: Un-skip the VT namespace architecture test**

In `tests/Ntilde.Architecture.Tests/NamespaceAlignmentTests.cs`, remove the `Skip = "..."` from `All_VT_types_use_Ntilde_VT_namespace`.

```powershell
scripts/build.ps1 test --filter "FullyQualifiedName~NamespaceAlignmentTests.All_VT"
```

Expected: passes.

- [ ] **Step 8: Commit**

```powershell
git add src/Ntilde.VT/ src/**/*.cs tests/**/*.cs tests/Ntilde.Architecture.Tests/
git commit -m "refactor(vt): align namespace to assembly (Ntilde.Core -> Ntilde.VT)"
```

### Task 3.2 — Rename Replay namespace (`Ntilde.Core.Replay` → `Ntilde.Replay`)

Same procedure as Task 3.1, scoped to `src/Ntilde.Replay/`. Mapping:

| Old | New |
|---|---|
| `Ntilde.Core.Replay` (defined in Replay project only) | `Ntilde.Replay` |

- [ ] **Step 1: Confirm no other namespace patterns exist in Replay**

```powershell
Get-ChildItem -Recurse -Path src/Ntilde.Replay -Filter *.cs | `
  Select-String -Pattern '^namespace ' | Select-Object -ExpandProperty Line | Sort-Object -Unique
```

Expected (per review): only `namespace Ntilde.Core.Replay`.

- [ ] **Step 2: Rename namespace declarations**

`namespace Ntilde.Core.Replay` → `namespace Ntilde.Replay`.

- [ ] **Step 3: Build and fix consumer `using` errors**

```powershell
scripts/build.ps1 build Ntilde.sln
```

Expected: consumers (Pty references Replay; App references Replay; tests) will need `using Ntilde.Replay;`.

- [ ] **Step 4: Run tests**

```powershell
scripts/build.ps1 test
```

- [ ] **Step 5: Un-skip the Replay namespace architecture test**

Remove the `Skip` from `All_Replay_types_use_Ntilde_Replay_namespace`. Run:

```powershell
scripts/build.ps1 test --filter "FullyQualifiedName~NamespaceAlignmentTests.All_Replay"
```

Expected: passes.

- [ ] **Step 6: Commit**

```powershell
git add src/Ntilde.Replay/ src/**/*.cs tests/**/*.cs tests/Ntilde.Architecture.Tests/
git commit -m "refactor(replay): align namespace to assembly (Ntilde.Core.Replay -> Ntilde.Replay)"
```

### Task 3.3 — Rename Rendering namespace (`Ntilde.Core` → `Ntilde.Rendering`)

Same procedure. Per review, every file in `src/Ntilde.Rendering/` uses `namespace Ntilde.Core`.

- [ ] **Step 1: Rename `namespace Ntilde.Core` → `namespace Ntilde.Rendering` in every file under `src/Ntilde.Rendering/`**

- [ ] **Step 2: Build solution, fix consumer `using` errors**

`App` is the main consumer; expect to add `using Ntilde.Rendering;` to many files in `src/Ntilde.App/`.

- [ ] **Step 3: Run tests**

```powershell
scripts/build.ps1 test
```

- [ ] **Step 4: Un-skip `All_Rendering_types_use_Ntilde_Rendering_namespace`. Verify it passes.**

- [ ] **Step 5: Commit**

```powershell
git commit -m "refactor(rendering): align namespace to assembly"
```

### Task 3.4 — Rename Pty namespace (`Ntilde.Core` → `Ntilde.Pty`)

Same procedure. Affects 6 files under `src/Ntilde.Pty/`.

**Note:** `ITerminalSession.cs` keeps its current shape for now — Phase 5 will redesign the interface. Only the namespace changes here.

- [ ] **Step 1: Rename `namespace Ntilde.Core` → `namespace Ntilde.Pty` in every file under `src/Ntilde.Pty/`**

- [ ] **Step 2: Build solution, fix consumer `using` errors**

Heavy consumer: `Ntilde.Core` (because `Core → Pty`), plus `App`.

- [ ] **Step 3: Run tests, un-skip Pty namespace test, commit**

```powershell
git commit -m "refactor(pty): align namespace to assembly"
```

### Task 3.5 — Audit `Ntilde.Core` namespace

After tasks 3.1–3.4, the only project legitimately using `Ntilde.Core` as its namespace is the `Ntilde.Core` assembly itself.

- [ ] **Step 1: Sanity-check that no other assembly still uses `Ntilde.Core`**

```powershell
foreach ($p in 'Ntilde.VT','Ntilde.Replay','Ntilde.Rendering','Ntilde.Pty') {
  $hits = Get-ChildItem -Recurse -Path "src/$p" -Filter *.cs |
          Select-String -Pattern '^\s*namespace Ntilde\.Core(\.|;|\s|$)'
  if ($hits) { Write-Host "LEAK in $p"; $hits }
}
```

Expected: no output. If any leak, repeat the relevant sub-phase.

- [ ] **Step 2: Add an architecture test that locks in the rule going forward**

In `tests/Ntilde.Architecture.Tests/NamespaceAlignmentTests.cs`, add:

```csharp
[Fact]
public void Only_the_Core_assembly_uses_Ntilde_Core_namespace()
{
    foreach (var asmName in new[] { "Ntilde.VT", "Ntilde.Replay",
                                     "Ntilde.Rendering", "Ntilde.Pty" })
    {
        var result = Types.InAssembly(LoadByName(asmName))
            .That().ArePublic()
            .Should()
            .NotResideInNamespaceStartingWith("Ntilde.Core")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"{asmName} must not use Ntilde.Core namespace. " +
            $"Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
```

- [ ] **Step 3: Run the new test, verify it passes**

```powershell
scripts/build.ps1 test --filter "FullyQualifiedName~Only_the_Core_assembly"
```

- [ ] **Step 4: Commit**

```powershell
git commit -am "test(architecture): lock in namespace-assembly alignment as a rule"
```

---

## Phase 4 — Test Stack Unification & Missing Test Projects

**Outcome:** A `Ntilde.VT.Tests` and `Ntilde.Rendering.Tests` exist as thin, fast unit suites; the existing test projects are renamed to match what they actually test; all of them use xUnit v3.

xUnit v3 unification was done as part of Task 1.1 step 3. This phase adds the missing projects and fixes naming.

### Task 4.1 — Rename `Ntilde.Tests` to `Ntilde.App.Tests`

The current `Ntilde.Tests` references `App` + `Cli` + `Conformance`, so it's an *App-level* integration suite. The name should reflect that.

**Files:**
- Rename folder: `tests/Ntilde.Tests/` → `tests/Ntilde.App.Tests/`
- Rename csproj: `Ntilde.Tests.csproj` → `Ntilde.App.Tests.csproj`
- Modify: `Ntilde.sln`
- Modify: any `InternalsVisibleTo` referring to `Ntilde.Tests`
- Modify: any CI script referring to it

- [ ] **Step 1: Find all references to the old name**

```powershell
Get-ChildItem -Recurse -File | Select-String -Pattern 'Ntilde\.Tests' -List | Select-Object Path
```

Expected hits include:
- `Ntilde.sln`
- `src/Ntilde.App/Ntilde.App.csproj` (`<InternalsVisibleTo Include="Ntilde.Tests" />`)
- `ci/run.ps1` / `ci/run.sh` (if they filter by project name)
- Possibly GitHub Actions workflow files under `.github/`

- [ ] **Step 2: Rename the directory and project file**

```powershell
git mv tests/Ntilde.Tests tests/Ntilde.App.Tests
git mv tests/Ntilde.App.Tests/Ntilde.Tests.csproj tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj
```

- [ ] **Step 3: Update the sln**

```powershell
dotnet sln Ntilde.sln remove tests/Ntilde.App.Tests/Ntilde.Tests.csproj 2>$null
dotnet sln Ntilde.sln add tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj
```

If `sln remove` fails (because the old path no longer exists post-rename), edit `Ntilde.sln` directly and replace `Ntilde.Tests` with `Ntilde.App.Tests` (paths and project name; **leave GUIDs unchanged**).

- [ ] **Step 4: Update `InternalsVisibleTo`**

In `src/Ntilde.App/Ntilde.App.csproj`:

```xml
<!-- before -->
<InternalsVisibleTo Include="Ntilde.Tests" />
<!-- after -->
<InternalsVisibleTo Include="Ntilde.App.Tests" />
```

- [ ] **Step 5: Update CI/script references**

Grep results from step 1; update each file.

- [ ] **Step 6: Build and run tests**

```powershell
scripts/build.ps1 build
scripts/build.ps1 test
```

- [ ] **Step 7: Commit**

```powershell
git commit -am "test: rename Ntilde.Tests to Ntilde.App.Tests (matches reference set)"
```

### Task 4.2 — Add `Ntilde.VT.Tests`

This is the missing fast unit suite for the terminal engine. Many existing tests under `Ntilde.App.Tests` (parser, buffer, reflow) are candidates for migration here.

**Files:**
- Create: `tests/Ntilde.VT.Tests/Ntilde.VT.Tests.csproj`
- Create: `tests/Ntilde.VT.Tests/SmokeTest.cs`
- Modify: `Ntilde.sln`
- Modify: `src/Ntilde.VT/Ntilde.VT.csproj` (add `InternalsVisibleTo`)

- [ ] **Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Ntilde.VT\Ntilde.VT.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create a smoke test that exercises the parser**

```csharp
using Ntilde.VT;

namespace Ntilde.VT.Tests;

public class SmokeTest
{
    [Fact]
    public void Parser_writes_plain_ASCII_to_buffer()
    {
        var buffer = new TerminalBuffer(cols: 10, rows: 1);
        var parser = new AnsiParser(buffer);

        parser.Feed("hello"u8);

        // Read the first row's text. Use whatever public snapshot API exists today;
        // if there isn't one, this test surfaces the gap.
        var snapshot = buffer.TakeSnapshot();
        Assert.StartsWith("hello", snapshot.RowAt(0).PlainText());
    }
}
```

If `Feed`, `TakeSnapshot`, `RowAt`, `PlainText` don't exist with those names, this test fails to compile — that's a **valuable signal** about the VT public surface. Adapt to whatever the real surface is; do not invent APIs. Document what you found in the commit message.

- [ ] **Step 3: Add `InternalsVisibleTo`**

In `src/Ntilde.VT/Ntilde.VT.csproj`:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="Ntilde.VT.Tests" />
</ItemGroup>
```

- [ ] **Step 4: Add to sln**

```powershell
dotnet sln Ntilde.sln add tests/Ntilde.VT.Tests/Ntilde.VT.Tests.csproj
```

- [ ] **Step 5: Build and run**

```powershell
scripts/build.ps1 build tests/Ntilde.VT.Tests
scripts/build.ps1 test --filter "FullyQualifiedName~Ntilde.VT.Tests"
```

- [ ] **Step 6: Commit**

```powershell
git add tests/Ntilde.VT.Tests/ src/Ntilde.VT/Ntilde.VT.csproj Ntilde.sln
git commit -m "test: add Ntilde.VT.Tests fast unit suite"
```

### Task 4.3 — Add `Ntilde.Rendering.Tests`

**Files:**
- Create: `tests/Ntilde.Rendering.Tests/Ntilde.Rendering.Tests.csproj`
- Create: `tests/Ntilde.Rendering.Tests/GlyphCacheTests.cs`
- Modify: `Ntilde.sln`
- Modify: `src/Ntilde.Rendering/Ntilde.Rendering.csproj` (add `InternalsVisibleTo`)

- [ ] **Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Ntilde.Rendering\Ntilde.Rendering.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create one smoke test**

```csharp
using Ntilde.Rendering;

namespace Ntilde.Rendering.Tests;

public class GlyphCacheTests
{
    [Fact]
    public void GlyphCache_can_be_constructed()
    {
        // Replace with the real ctor signature from src/Ntilde.Rendering/GlyphCache.cs.
        // If it requires a Skia context, document the dependency in the test.
        var cache = new GlyphCache();
        Assert.NotNull(cache);
    }
}
```

Adapt to the real `GlyphCache` constructor. If the type genuinely cannot be constructed without a live Skia GPU context, leave a Skip with that reason — this is itself a signal that `Rendering` needs a thin "headless" seam.

- [ ] **Step 3: Add `InternalsVisibleTo` to `Ntilde.Rendering.csproj`**

```xml
<ItemGroup>
  <InternalsVisibleTo Include="Ntilde.Rendering.Tests" />
</ItemGroup>
```

- [ ] **Step 4: Add to sln, build, run, commit**

```powershell
dotnet sln Ntilde.sln add tests/Ntilde.Rendering.Tests/Ntilde.Rendering.Tests.csproj
scripts/build.ps1 build tests/Ntilde.Rendering.Tests
scripts/build.ps1 test --filter "FullyQualifiedName~Ntilde.Rendering.Tests"
git add tests/Ntilde.Rendering.Tests/ src/Ntilde.Rendering/Ntilde.Rendering.csproj Ntilde.sln
git commit -m "test: add Ntilde.Rendering.Tests scaffold"
```

---

## Phase 5 — `ITerminalSession` Decomposition

**Outcome:** `ITerminalSession` is split into focused interfaces. The VT dependency from Pty is removed (`AttachBuffer` becomes the orchestration layer's job, not the session's). The Pty namespace test for *no dependency on VT* (skipped in Phase 2) is un-skipped here.

**Risk:** This phase changes a public interface. Every implementer (`RustPtySession` and any SSH-backed session in `Core`) and every consumer (`SessionManager`, `TerminalView`) must be updated. Do not merge mid-phase.

### Task 5.1 — Define the split interfaces

**Files:**
- Create: `src/Ntilde.Pty/ITerminalIO.cs`
- Create: `src/Ntilde.Pty/ITerminalLifecycle.cs`
- Create: `src/Ntilde.Pty/ITerminalShellMetadata.cs`
- Create: `src/Ntilde.Pty/ITerminalRecorder.cs`
- Modify: `src/Ntilde.Pty/ITerminalSession.cs` — becomes a composite alias for backward compat

- [ ] **Step 1: Define `ITerminalIO`**

```csharp
using System;

namespace Ntilde.Pty;

/// <summary>Raw byte I/O for a terminal session.</summary>
public interface ITerminalIO
{
    void SendInput(ReadOnlySpan<byte> data);
    event Action<ReadOnlyMemory<byte>>? OnOutputReceived;
}
```

**Rationale (Section D):** byte-spans, not strings. The PTY produces raw bytes; converting to UTF-16 string at the boundary loses split-multibyte semantics and embedded NULs.

- [ ] **Step 2: Define `ITerminalLifecycle`**

```csharp
using System;

namespace Ntilde.Pty;

/// <summary>Session lifetime: identity, resize, exit, child-process state.</summary>
public interface ITerminalLifecycle : IDisposable
{
    Guid Id { get; }
    void Resize(int cols, int rows);
    bool IsProcessRunning { get; }
    bool HasActiveChildProcesses { get; }
    int? ExitCode { get; }
    event Action<int>? OnExit;
}
```

- [ ] **Step 3: Define `ITerminalShellMetadata`**

```csharp
namespace Ntilde.Pty;

/// <summary>Descriptive metadata about the process backing this session.</summary>
public interface ITerminalShellMetadata
{
    string ShellCommand { get; }
    string? ShellArguments { get; }
}
```

- [ ] **Step 4: Define `ITerminalRecorder`**

```csharp
namespace Ntilde.Pty;

/// <summary>Replay/recording control for a session.</summary>
public interface ITerminalRecorder
{
    bool IsRecording { get; }
    void StartRecording(string filePath);
    void StopRecording();
}
```

- [ ] **Step 5: Redefine `ITerminalSession` as a composite (transitional)**

```csharp
namespace Ntilde.Pty;

/// <summary>
/// Composite interface preserved during the migration from a single god-interface.
/// New code should depend on the narrowest sub-interface it actually needs
/// (ITerminalIO, ITerminalLifecycle, ITerminalShellMetadata, ITerminalRecorder).
/// `AttachBuffer` and `TakeSnapshot` have moved to the orchestration layer
/// (Ntilde.Core's session-wiring code) and are NOT part of this contract.
/// </summary>
public interface ITerminalSession
    : ITerminalIO, ITerminalLifecycle, ITerminalShellMetadata, ITerminalRecorder
{
}
```

**Key change:** `AttachBuffer(TerminalBuffer)` and `TakeSnapshot()` are **removed** from this contract. That's what severs the Pty→VT dependency.

- [ ] **Step 6: Build to find compile errors**

```powershell
scripts/build.ps1 build Ntilde.sln
```

Expected errors:
1. `RustPtySession` no longer compiles because it implements removed members.
2. Any caller of `session.AttachBuffer(...)` or `session.TakeSnapshot()` fails.
3. `SendInput(string)` callers fail (signature changed to bytes).
4. `OnOutputReceived` subscribers receive `ReadOnlyMemory<byte>` not `string`.

Don't fix yet — go through them in tasks 5.2–5.4.

### Task 5.2 — Migrate `RustPtySession`

**Files:**
- Modify: `src/Ntilde.Pty/RustPtySession.cs`

- [ ] **Step 1: Remove `AttachBuffer` and `TakeSnapshot` implementations from `RustPtySession`**

These methods (if non-trivial in the current implementation) probably:
- Cache the buffer reference
- Forward bytes through an `AnsiParser` that writes to the buffer
- Trigger a snapshot on demand

That parser-driving logic does **not** belong here. Move it to a new helper in the next task. For now, delete the methods.

- [ ] **Step 2: Change `SendInput(string)` → `SendInput(ReadOnlySpan<byte>)`**

Find the rust-FFI write call. Today the path is `string → UTF8 bytes → FFI`. After the change it's just `bytes → FFI`.

- [ ] **Step 3: Change `OnOutputReceived` from `Action<string>` to `Action<ReadOnlyMemory<byte>>`**

The read loop (rust → managed) should now emit the bytes it actually has, not a UTF-16 decode.

- [ ] **Step 4: Build the Pty project alone**

```powershell
scripts/build.ps1 build src/Ntilde.Pty
```

Expected: green. `Pty` no longer needs to reference VT.

- [ ] **Step 5: Remove the VT project reference from Pty.csproj**

```xml
<!-- Delete this line -->
<ProjectReference Include="..\Ntilde.VT\Ntilde.VT.csproj" />
```

But keep `..\Ntilde.Replay\Ntilde.Replay.csproj` only if `ITerminalRecorder` implementations still depend on Replay types. If not (recording is just a file path + byte logging at this layer), remove that too — but be careful: the `Replay` project itself depends on VT, so removing Replay from Pty *strengthens* the boundary.

If Replay types are actually needed by `RustPtySession` (look for `using Ntilde.Replay`), keep the reference but plan a follow-up to invert it.

- [ ] **Step 6: Re-build the solution**

```powershell
scripts/build.ps1 build Ntilde.sln
```

Expected: callers in `App` / `Core` will now break. Fix in the next task.

### Task 5.3 — Move buffer-wiring out of the session

The deleted `AttachBuffer` / `TakeSnapshot` logic was the seam between bytes and parsed state. It now lives in an *orchestration* layer — `Ntilde.Core` (which already references Pty and VT, so this is its natural home).

**Files:**
- Create: `src/Ntilde.Core/SessionBufferBinder.cs`
- Modify: `src/Ntilde.App/Core/SessionManager.cs` (or wherever sessions are instantiated)

- [ ] **Step 1: Create `SessionBufferBinder`**

```csharp
using Ntilde.Pty;
using Ntilde.VT;

namespace Ntilde.Core;

/// <summary>
/// Wires a raw byte session (ITerminalIO) into an AnsiParser/TerminalBuffer pair.
/// Owns the per-session parser instance. Disposing this unsubscribes from the session;
/// it does NOT dispose the session.
/// </summary>
public sealed class SessionBufferBinder : IDisposable
{
    private readonly ITerminalIO _io;
    private readonly AnsiParser _parser;
    private readonly TerminalBuffer _buffer;
    private bool _disposed;

    public TerminalBuffer Buffer => _buffer;

    public SessionBufferBinder(ITerminalIO io, TerminalBuffer buffer)
    {
        _io = io;
        _buffer = buffer;
        _parser = new AnsiParser(buffer);
        _io.OnOutputReceived += OnBytes;
    }

    public BufferSnapshot TakeSnapshot() => _buffer.TakeSnapshot();

    private void OnBytes(ReadOnlyMemory<byte> bytes) => _parser.Feed(bytes.Span);

    public void Dispose()
    {
        if (_disposed) return;
        _io.OnOutputReceived -= OnBytes;
        _disposed = true;
    }
}
```

Adapt `BufferSnapshot`, `Feed`, `TakeSnapshot` to the real public API of `TerminalBuffer` / `AnsiParser`.

- [ ] **Step 2: Update every place that used to call `session.AttachBuffer(buffer)`**

Replace:

```csharp
session.AttachBuffer(buffer);
// later: var snapshot = session.TakeSnapshot();
```

with:

```csharp
var binder = new SessionBufferBinder(session, buffer);
// later: var snapshot = binder.TakeSnapshot();
// on tear-down: binder.Dispose();
```

The session and the binder have independent lifetimes; the owner (probably `SessionManager` or `TerminalPane`) should hold both.

- [ ] **Step 3: Update `SendInput(string)` callers**

Find every `session.SendInput("...")` and replace with `session.SendInput("..."u8)` for ASCII, or `Encoding.UTF8.GetBytes(...)` for runtime strings. Decision point goes back to the caller, which knows whether the input is a literal escape sequence or user text.

- [ ] **Step 4: Update `OnOutputReceived` subscribers**

Callers that handled `string` output now handle `ReadOnlyMemory<byte>`. If they were just forwarding to a parser, they should use a `SessionBufferBinder`. If they were displaying / logging the bytes, decide on the encoding at the consumer.

- [ ] **Step 5: Build and run all tests**

```powershell
scripts/build.ps1 build
scripts/build.ps1 test
```

Expected: green. Some App-level tests may need adjustment if they asserted on `string` output — convert to `Encoding.UTF8.GetBytes(...)` or assert on the buffer snapshot rather than the raw stream.

### Task 5.4 — Un-skip the Pty-no-VT architecture test

- [ ] **Step 1: Remove the `Skip` from `Pty_must_not_depend_on_Vt`**

In `tests/Ntilde.Architecture.Tests/LayeringTests.cs`, change:

```csharp
[Fact(Skip = "Known violation — fixed in Phase 5 of architecture-foundation-plan")]
public void Pty_must_not_depend_on_Vt()
```

to:

```csharp
[Fact]
public void Pty_must_not_depend_on_Vt()
```

- [ ] **Step 2: Run the architecture tests**

```powershell
scripts/build.ps1 test --filter "FullyQualifiedName~LayeringTests"
```

Expected: all pass.

- [ ] **Step 3: Commit the whole phase as a single coherent change**

```powershell
git add src/Ntilde.Pty/ src/Ntilde.Core/SessionBufferBinder.cs src/Ntilde.App/ tests/Ntilde.Architecture.Tests/
git commit -m "refactor(pty): split ITerminalSession; move buffer wiring out of Pty; remove Pty->VT dependency"
```

---

## Phase 6 — Documentation Realignment

**Outcome:** `ARCHITECTURE.md` and `MODULE_OWNERSHIP.md` describe what the codebase actually is, not what it was before two refactors ago.

### Task 6.1 — Rewrite `ARCHITECTURE.md`

**Files:**
- Modify: `docs/ARCHITECTURE.md`

- [ ] **Step 1: Rewrite the layering diagram to match reality**

Replace the 4-layer ASCII diagram with the 8-assembly graph from `README.md` (or update the README's graph if it's stale). Key updates:
- `Ntilde.Core` is a **platform-utilities** library (Input, Paths, Process, SSH), not the terminal engine.
- `Ntilde.VT` is the terminal engine (parser + buffer state).
- `Ntilde.Pty` is the byte transport; it no longer interprets VT (post-Phase 5).
- `Ntilde.Rendering` holds Skia primitives; the Avalonia renderer (`TerminalView`, `TerminalDrawOperation`) lives in `App` today and is slated for extraction in a follow-up.
- `Ntilde.Replay` is record/playback over byte streams + buffer snapshots.

- [ ] **Step 2: Update file references throughout**

Replace every occurrence of:
- `Core/AnsiParser.cs` → `src/Ntilde.VT/AnsiParser.cs`
- `Core/TerminalBuffer.cs` → `src/Ntilde.VT/TerminalBuffer*.cs`
- `Core/TerminalView.cs` → (current: `src/Ntilde.App/Core/TerminalView.cs`; future: `src/Ntilde.Rendering/`)
- `Core/TerminalDrawOperation.cs` → `src/Ntilde.App/Core/TerminalDrawOperation.cs`
- `Core/RustPtySession.cs` → `src/Ntilde.Pty/RustPtySession.cs`

- [ ] **Step 3: Add a section documenting the architecture-tests safety net**

Point readers at `tests/Ntilde.Architecture.Tests/` and explain that layering rules are enforced by tests, not by convention.

- [ ] **Step 4: Add explicit "Known Tech Debt" section**

List the deferred work from Phases 7+ so future contributors know these are tracked, not unknown:
- Renderer composition still in `App/Core/` (extraction planned)
- SSH fragmented across `Core/Ssh` and `App/` (extraction planned)
- CommandAssist nested inside `App` (extraction planned)
- `Cli → App` reference inversion (Bootstrap library planned)
- `MainWindow.axaml.cs` 5,259 LOC, `TerminalPane.axaml.cs` 2,572 LOC, `SettingsWindow.axaml.cs` 1,672 LOC

- [ ] **Step 5: Commit**

```powershell
git add docs/ARCHITECTURE.md
git commit -m "docs(architecture): rewrite to match current 8-assembly layout"
```

### Task 6.2 — Rewrite `MODULE_OWNERSHIP.md`

**Files:**
- Modify: `docs/MODULE_OWNERSHIP.md`

- [ ] **Step 1: One section per assembly, file-paths anchored to today**

Template per module:

```markdown
## Ntilde.<X> (`src/Ntilde.<X>/`)

**Namespace:** `Ntilde.<X>[.<Sub>]`
**Depends on:** [list of project refs]
**Public surface:** [1-line summary of the main entry points]

**Owns**
- ...

**Invariants** (enforced by `tests/Ntilde.Architecture.Tests/` and unit tests)
- ...

**Test authority**
- Primary: `tests/Ntilde.<X>.Tests/` (if it exists)
- Integration: `tests/Ntilde.App.Tests/...`
```

Fill in all 8 modules: VT, Replay, Rendering, Pty, Core, App, Cli, Conformance.

- [ ] **Step 2: Commit**

```powershell
git add docs/MODULE_OWNERSHIP.md
git commit -m "docs(ownership): rewrite per-assembly ownership map"
```

---

## Phases 7+ — Follow-up Plans (deferred)

The remaining findings require their own detailed plans. Each is a multi-day effort. They share a precondition: **phases 1–6 of this plan must be complete first** — without architecture tests, namespace alignment, and a thin VT test suite, every extraction carries unacceptable regression risk.

### Phase 7 — Renderer Composition Extraction
**Plan filename:** `docs/plans/YYYY-MM-DD-renderer-composition-extraction-plan.md`
**Scope:**
- Move `src/Ntilde.App/Core/TerminalView.cs` (1,912 LOC) and `src/Ntilde.App/Core/TerminalDrawOperation.cs` (2,723 LOC) into `src/Ntilde.Rendering/` behind a clean public surface.
- Leave only a thin Avalonia binding shell in App.
- Add an architecture test: `Rendering` may take a buffer snapshot in and produce drawing commands out; it may not reference Avalonia control types.
- Both files are huge — expect to split each into 3–6 collaborators during the move.
- Addresses: review Section E (TerminalView/DrawOperation mis-located), Section I (Renderer composition module), Section J (code-behind size).

### Phase 8 — SSH Module Extraction
**Plan filename:** `docs/plans/YYYY-MM-DD-ssh-module-extraction-plan.md`
**Scope:**
- Create `src/Ntilde.Ssh/` (or `.Remote/`).
- Pull together: `src/Ntilde.Core/Ssh/{Interactions,Launch,Models,Native,OpenSsh,Sessions,Storage,Transport}`, `src/Ntilde.App/Services/Ssh/`, `src/Ntilde.App/Core/{SftpService,VaultService,SshAskPassCommand}.cs`.
- Keep Avalonia/UI types out of this assembly. ViewModels and Views stay in App.
- The new module references VT for snapshot-related types (SSH session feeds a buffer through the same path) and Pty for `ITerminalIO`/`ITerminalLifecycle`.
- Add architecture test: `Ntilde.Ssh` may not reference `Avalonia`.
- Addresses: review Section E (SSH fragmentation), Section I (SSH module).

### Phase 9 — CommandAssist Module Extraction
**Plan filename:** `docs/plans/YYYY-MM-DD-commandassist-module-extraction-plan.md`
**Scope:**
- Move `src/Ntilde.App/CommandAssist/{Application,Domain,Models,Storage,ShellIntegration}` into `src/Ntilde.CommandAssist/`.
- Leave `ViewModels/` and `Views/` in App (UI binding stays UI).
- Add architecture test: the new assembly must not reference Avalonia.
- Already has internal Application/Domain/Storage layering — extraction will surface whether the layering is real or aspirational.
- Addresses: review Section E (CommandAssist sub-architecture), Section I (CommandAssist module).

### Phase 10 — CLI Reference Inversion via Bootstrap Library
**Plan filename:** `docs/plans/YYYY-MM-DD-bootstrap-library-and-cli-inversion-plan.md`
**Scope:**
- Create `src/Ntilde.Bootstrap/`.
- Move `src/Ntilde.App/Core/{VtReportCli,VtReportCommand,CliConsoleBindings}.cs` plus any other code currently in App that is used solely by Cli into Bootstrap.
- Change references: `Cli → Bootstrap` and `App → Bootstrap`. Remove `Cli → App` and the `BuildCliShim` / `PublishCliShim` MSBuild targets in `App.csproj`.
- Remove `<InternalsVisibleTo Include="Ntilde.Cli" />` from App.
- Update CI to publish CLI directly, not via App's shim copy.
- Addresses: review Section G (CLI build dance).

### Phase 11 — Cleanups Roll-up
**Plan filename:** `docs/plans/YYYY-MM-DD-architecture-cleanups-rollup-plan.md`
**Scope:**
- Decide whether `Ntilde.Core` should be renamed to `Ntilde.Platform` (it's a platform-utilities library, not the terminal core). One-shot find-replace.
- Review `src/Ntilde.VT/TerminalBuffer.*.cs` (10 partial files, ~5K LOC) and propose splitting into collaborators (`WritePath`, `ReflowEngine`, `ThreadingAndInvalidation`, `TabStops` are obvious candidates).
- Decompose the three giant code-behinds: `MainWindow.axaml.cs` (5,259 LOC), `TerminalPane.axaml.cs` (2,572 LOC), `SettingsWindow.axaml.cs` (1,672 LOC) into proper ViewModels + Services. Each likely deserves its own plan.
- Consolidate `App/Services/` / `App/Models/` / `App/UI/` conventions or collapse the empty subfolders.
- Move `src/Ntilde.Conformance` to `tools/` if it's a developer tool, or fully integrate into the sln-driven build if it's library-shaped.
- Addresses: review Section H (Conformance orphaning), Section J (smaller items).

---

## Self-Review Notes

**Spec coverage** (against `docs/plans/2026-05-28-architecture-module-boundaries-review.md`):

| Finding | Addressed in |
|---|---|
| A. Docs vs reality | Phase 6 |
| B. "Core" overloaded | Phase 3 (namespaces), Phase 11 (Core rename) |
| C. PTY/VT inversion | Phase 5 (interface split removes the Pty→VT edge) |
| D. ITerminalSession kitchen sink | Phase 5 |
| E. App god-module | Phases 7, 8, 9, 11 (deferred) |
| F. Sln/test hygiene | Phase 1.3, Phase 4 |
| G. CLI build dance | Phase 10 (deferred) |
| H. Conformance orphan | Phase 1.3 (sln), Phase 11 (location) |
| I. Missing modules | Phases 7, 8, 9, 10 (deferred) |
| J. Misc | Phase 11 (deferred) |
| "Most concentrated risk" (namespace collapse) | Phase 2 (architecture tests) + Phase 3 (renames) |

**Type-consistency check:** `ITerminalIO`, `ITerminalLifecycle`, `ITerminalShellMetadata`, `ITerminalRecorder`, `ITerminalSession` (composite), `SessionBufferBinder` are used consistently in Phase 5 tasks 5.1–5.4. `BufferSnapshot` referenced in Phase 4.2 step 2 and Phase 5.3 step 1 — its real name in the VT public surface needs to be checked when the task is executed.

**Placeholder scan:** No "TBD" / "implement later". Two adapt-to-reality callouts (Task 4.2 step 2 and 4.3 step 2) tell the engineer to match real public APIs rather than invent them — those are signal-generating tests, not placeholders.

**Open question for the executor:** When Phase 3 renames the VT namespace, the `Ntilde.Core.Replay` namespace conflict (Task 3.1 step 2) needs a content-driven decision the executor makes by inspecting the files. That's documented in the task; do not skip the inspection.
