# VT Capability Contract Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make supported CSI cursor capabilities a single machine-readable contract that CI proves against the real parser and developer-facing tooling.

**Architecture:** Add a zero-project-reference `NovaTerminal.VtContract` leaf with an embedded JSON manifest and strict loader. Conformance tooling validates each manifest entry against the Markdown matrix, MCP explanations consume the same catalog, and VT tests execute every supported contract case through `AnsiParser`; the parser hot path remains unchanged.

**Tech Stack:** .NET 10, C#, System.Text.Json, xUnit v3, GitHub Actions, existing NovaTerminal build wrappers.

---

### Task 1: Add the catalog contract test surface

**Files:**
- Create: `src/NovaTerminal.VtContract/NovaTerminal.VtContract.csproj`
- Create: `src/NovaTerminal.VtContract/vt-capabilities.json`
- Create: `src/NovaTerminal.VtContract/VtCapabilityCatalog.cs`
- Modify: `tests/NovaTerminal.VT.Tests/NovaTerminal.VT.Tests.csproj`
- Create: `tests/NovaTerminal.VT.Tests/VtCapabilityCatalogTests.cs`

**Step 1: Write the failing validation tests**

Add tests calling the wished-for `VtCapabilityCatalog.Parse` API. Assert that valid JSON returns deterministic `VtCapability` records and that duplicate keys, invalid support values, missing descriptions, and supported entries without `evidencePath` or `contractCase` throw `VtCapabilityManifestException` naming the bad entry.

**Step 2: Run the tests to verify RED**

Run:
`rtk pwsh -NoProfile -File scripts/build.ps1 test tests/NovaTerminal.VT.Tests/NovaTerminal.VT.Tests.csproj --filter FullyQualifiedName~VtCapabilityCatalogTests`

Expected: build failure because `NovaTerminal.VtContract` and `VtCapabilityCatalog` do not exist.

**Step 3: Implement the minimal catalog leaf**

Create a warning-clean zero-reference class library. Embed `vt-capabilities.json` as `NovaTerminal.VtContract.vt-capabilities.json`. Implement:

```csharp
public enum VtSupport { Supported, Partial, Unsupported }

public sealed record VtCapability(
    string Key,
    string Mnemonic,
    VtSupport Support,
    string Description,
    string MatrixFeature,
    string? EvidencePath,
    string? ContractCase);

public static class VtCapabilityCatalog
{
    public static IReadOnlyList<VtCapability> All { get; }
    public static IReadOnlyList<VtCapability> Parse(string json);
}
```

Seed the first contract tranche with `CSI:E`/CNL, `CSI:F`/CPL, and `CSI:G`/CHA. Mark all three supported and point them at concrete parser tests and unique matrix features.

**Step 4: Run the focused tests to verify GREEN**

Run the command from Step 2. Expected: all catalog tests pass with no warnings.

**Step 5: Commit**

Commit as `feat(vt): add capability contract catalog`.

### Task 2: Prove manifest claims against the real parser

**Files:**
- Modify: `tests/NovaTerminal.VT.Tests/CursorLinePositioningTests.cs`
- Create: `tests/NovaTerminal.VT.Tests/VtCapabilityContractTests.cs`

**Step 1: Write the failing parser-contract tests**

Enumerate every `Supported` catalog entry and dispatch its `ContractCase` to a deterministic real-parser assertion. Cover CNL, CPL, and CHA. For the CNL/CPL streams assert omitted and zero defaults, count movement, column reset, viewport/margin clamping, private/intermediate no-op behavior, and equivalence when the escape sequence is split at every byte boundary. Add a guard that fails whenever a supported manifest entry has no registered contract case.

**Step 2: Run the tests to verify RED**

Run:
`rtk pwsh -NoProfile -File scripts/build.ps1 test tests/NovaTerminal.VT.Tests/NovaTerminal.VT.Tests.csproj --filter FullyQualifiedName~VtCapabilityContractTests`

Expected: failure because the catalog's contract-case registry is not yet implemented and CHA lacks a registered contract assertion.

**Step 3: Implement the minimal test contract registry**

Add test-only handlers for `cursor-next-line`, `cursor-previous-line`, and `cursor-horizontal-absolute`. Reuse public `AnsiParser.Process` and `TerminalBuffer` state only; add no production parser API.

**Step 4: Run focused and full VT tests**

Run the command from Step 2, then:
`rtk pwsh -NoProfile -File scripts/build.ps1 test tests/NovaTerminal.VT.Tests/NovaTerminal.VT.Tests.csproj`

Expected: all VT tests pass.

**Step 5: Commit**

Commit as `test(vt): enforce supported cursor contracts`.

### Task 3: Make conformance validation consume the catalog

**Files:**
- Modify: `src/NovaTerminal.Conformance/NovaTerminal.Conformance.csproj`
- Modify: `src/NovaTerminal.Conformance/VtConformanceReportTool.cs`
- Modify: `tests/NovaTerminal.Platform.Tests/Conformance/VtConformanceToolTests.cs`
- Modify: `docs/vt_coverage_matrix.md`
- Modify: `src/NovaTerminal.App/Resources/vt-conformance-report.json`

**Step 1: Write failing matrix-contract tests**

Add temporary-repository tests proving validation reports errors when a catalog feature is absent, when its matrix status disagrees with the manifest support value, when two capabilities share one matrix feature, or when the evidence path is missing. Add a current-repository assertion that all manifest entries match unique rows with existing evidence.

**Step 2: Run the tests to verify RED**

Run:
`rtk pwsh -NoProfile -File scripts/build.ps1 test tests/NovaTerminal.Platform.Tests/NovaTerminal.Platform.Tests.csproj --filter FullyQualifiedName~VtConformanceToolTests`

Expected: the new assertions fail because `Generate` does not validate the capability catalog.

**Step 3: Implement catalog-to-matrix validation**

Reference `NovaTerminal.VtContract` from the conformance project. Validate unique `MatrixFeature` values, exact status agreement, and repository-relative evidence existence. Emit deterministic `VtConformanceIssue` codes and retain the existing report schema.

**Step 4: Correct the matrix and report**

Replace grouped `CHA/CPL/CNL (G/F/E)` with unique `CHA (G)`, `CPL (F)`, and `CNL (E)` supported rows linked to concrete tests. Regenerate the embedded report with the existing conformance CLI.

**Step 5: Run focused validation**

Run the focused tests, then:
`rtk pwsh -NoProfile -File scripts/build.ps1 run --project src/NovaTerminal.Conformance/NovaTerminal.Conformance.csproj -- --validate --check-report src/NovaTerminal.App/Resources/vt-conformance-report.json`

Expected: zero validation errors and a matching embedded report.

**Step 6: Commit**

Commit as `feat(vt): validate capability claims against matrix`.

### Task 4: Drive MCP explanations from the contract

**Files:**
- Modify: `src/NovaTerminal.McpServer/NovaTerminal.McpServer.csproj`
- Modify: `src/NovaTerminal.McpServer/Tools/VtTools.cs`
- Modify: `tests/NovaTerminal.McpServer.Tests/V2ToolsTests.cs`
- Modify: `tests/NovaTerminal.Architecture.Tests/ProjectFileLayeringTests.cs`
- Modify: `docs/MODULE_OWNERSHIP.md`

**Step 1: Write failing explanation tests**

Replace the stale unsupported assertions for `CSI E/F` with tests that require `CNL`/`CPL`, describe column-one movement, and contain no unsupported warning. Add a drift guard asserting every catalog key resolves through `ExplainEscapeSequence` with its mnemonic and support state.

**Step 2: Run the tests to verify RED**

Run:
`rtk pwsh -NoProfile -File scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests/NovaTerminal.McpServer.Tests.csproj --filter FullyQualifiedName~ExplainEscapeSequenceTests`

Expected: CNL/CPL tests fail because the curated table still says they are unhandled.

**Step 3: Implement shared explanations**

Reference the zero-dependency contract leaf from MCP. Remove E/F/G duplicates from `SequenceTable`; resolve catalog keys first and format support state from the typed record. Update the explicit architecture allowlist and module documentation to permit only this additional zero-reference leaf.

**Step 4: Run MCP and architecture tests**

Run the focused MCP tests and:
`rtk pwsh -NoProfile -File scripts/build.ps1 test tests/NovaTerminal.Architecture.Tests/NovaTerminal.Architecture.Tests.csproj`

Expected: all tests pass and the layering invariant names all permitted MCP leaf dependencies.

**Step 5: Commit**

Commit as `fix(mcp): source VT explanations from capability contract`.

### Task 5: Wire the contract into repository CI and solution metadata

**Files:**
- Modify: `NovaTerminal.sln`
- Modify: `.github/workflows/vt-conformance.yml`
- Modify: `.github/pull_request_template.md`
- Modify: `docs/ghostty-gaps/vt_conformance_tooling.md`

**Step 1: Write the failing workflow/documentation assertions**

Extend the existing architecture or tooling tests to assert the conformance workflow watches `src/NovaTerminal.VtContract/**` and the PR template requires contract evidence for parser support changes.

**Step 2: Run the focused tests to verify RED**

Run the relevant architecture/tooling test filter. Expected: failure because the workflow and template do not mention the contract.

**Step 3: Apply the minimal integration changes**

Add the leaf project to the solution, include its path in conformance workflow triggers, and document the catalog/contract-test update rule and regeneration command. Keep the checklist advisory text aligned with the new hard CI validation.

**Step 4: Run focused verification**

Run VT, MCP, architecture, platform conformance, and conformance CLI checks. Expected: all pass.

**Step 5: Commit**

Commit as `ci(vt): gate capability contract changes`.

### Task 6: Full verification and delivery

**Files:**
- Review: all changed files

**Step 1: Run full wrapped verification**

Run:

```powershell
rtk pwsh -NoProfile -File scripts/build.ps1 build NovaTerminal.sln -c Release
rtk pwsh -NoProfile -File scripts/build.ps1 test -c Release
```

Expected: build and tests pass with no new warnings. Also confirm the conformance CLI reports zero errors and the embedded report matches.

**Step 2: Review the complete diff**

Check `rtk git diff main...HEAD`, ensure no parser hot-path changes, no unrelated files, deterministic JSON, and updated ownership documentation.

**Step 3: Push and open the PR**

Push `codex/vt-capability-contract`, create a PR describing the single-source contract, parser evidence, and verification, then watch CI and Greptile. For each valid finding, add a failing regression test first and apply the minimal fix.
