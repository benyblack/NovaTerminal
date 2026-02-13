# NovaTerminal - Production Execution Plan

Date: 2026-02-13  
Status: Executed (PR1-PR4 complete)

## Execution Summary

- PR1 branch: `feature/pr1-auth-vault` -> merged to `main` via commit `1fabee7`
- PR2 branch: `feature/pr2-user-scoped-paths` -> merged to `main` via commit `5ed9993`
- PR3 branch: `feature/pr3-ci-parity-nightly` -> merged to `main` via commit `e50013f`
- PR4 branch: `feature/pr4-doc-sync` (this documentation sync)

### Verification Completed

- `dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release`
- `dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "Category=Replay"`
- `dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "Category=Stress|Category=Performance|Category=Latency"`
- `dotnet build tests/NovaTerminal.ExternalSuites/NovaTerminal.ExternalSuites.csproj -c Release`

---

## Scope Decisions (Locked)

1. Remove automatic password injection.
2. Move runtime writable data to user-scoped app data locations.
3. Use runtime-generated parity artifacts in CI (not checked-in fixture files).

---

## Objectives

- Close security and correctness gaps that block production readiness.
- Make behavior consistent across Windows/Linux/macOS.
- Strengthen CI signal so parity failures are real runtime regressions.

---

## Non-Objectives

- No redesign of terminal rendering architecture in this plan.
- No VT feature expansion beyond what is needed for this hardening pass.
- No plugin/API surface work.

---

## Workstream A - Auth Hardening (P0)

### Goal
Eliminate automatic credential submission triggered by terminal output text.

### Current Risk
- Password auto-send can be triggered by spoofed prompt content.
- Auto-sent input can be captured in replay recordings.

### Implementation
- Remove password prompt matching and delayed auto-send flow in `RustPtySession`.
- Remove session-level saved password field usage for interactive terminal auth.
- Keep normal manual password entry through terminal input.
- Keep key-based and agent-based SSH auth behavior unchanged.

### Files
- `NovaTerminal/Core/RustPtySession.cs`
- `NovaTerminal/Core/ITerminalSession.cs`
- `NovaTerminal/Controls/TerminalPane.axaml.cs`

### Acceptance Criteria
- No code path sends credentials based on output containing `password:`.
- SSH password auth still works manually via interactive terminal prompt.
- No credential auto-send events appear in replay data.

### Tests
- Add/adjust tests to assert:
  - no automated send on prompt-like output;
  - interactive password flow remains functional.

---

## Workstream B - Vault Key Canonicalization (P0)

### Goal
Use one canonical key format for storing/retrieving SSH secrets.

### Canonical Key
- `SSH:PROFILE:{profileId}`

### Migration Rule
- On read, try canonical key first.
- If missing, fallback to legacy keys:
  - `SSH:{profileName}:{user}@{host}`
  - `SSH:{user}@{host}`
  - `profile_{id}_password`
- If legacy value is found, re-save under canonical key.

### Files
- `NovaTerminal/SettingsWindow.axaml.cs`
- `NovaTerminal/Controls/TerminalPane.axaml.cs`
- `NovaTerminal/Core/VaultService.cs`

### Acceptance Criteria
- Save/read path uses canonical key consistently.
- Existing users with legacy keys do not lose access after upgrade.

### Tests
- Add migration coverage for each legacy key format.
- Add roundtrip tests for canonical key storage and retrieval.

---

## Workstream C - User-Scoped Writable Paths (P0/P1)

### Goal
Stop writing runtime data under application install/base directory.

### Path Policy
Use `Environment.SpecialFolder.LocalApplicationData` root:
- `<LocalAppData>/NovaTerminal/settings.json`
- `<LocalAppData>/NovaTerminal/themes/*.json`
- `<LocalAppData>/NovaTerminal/logs/debug.log`
- `<LocalAppData>/NovaTerminal/logs/startup_error.txt`
- `<LocalAppData>/NovaTerminal/sessions/last_session.json`
- `<LocalAppData>/NovaTerminal/recordings/*.rec`

### Implementation Pattern
- Introduce a shared path helper (single source of truth).
- Replace direct `AppDomain.CurrentDomain.BaseDirectory` writes.
- Ensure directory creation before write operations.

### Files
- `NovaTerminal/Core/TerminalSettings.cs`
- `NovaTerminal/Core/ThemeManager.cs`
- `NovaTerminal/Core/TerminalLogger.cs`
- `NovaTerminal/Core/SessionManager.cs`
- `NovaTerminal/Controls/TerminalPane.axaml.cs`
- `NovaTerminal/Program.cs`

### Migration (P1)
- One-time copy/merge from legacy locations if new location missing.
- Never overwrite newer destination files.

### Acceptance Criteria
- App functions correctly when install directory is read-only.
- Settings/themes/logs/session/recordings persist in user-scoped location.
- Existing data from legacy location is retained after first run.

### Tests
- Path resolution unit tests (all supported OSes).
- Migration behavior tests (legacy present vs absent).

---

## Workstream D - Runtime-Generated Parity Artifacts (P0)

### Goal
Compare outputs generated during CI runs, not checked-in fixture files.

### Implementation
- Replay/parity tests must emit generated snapshots to OS-specific artifact directory.
- Upload generated artifacts from each OS job.
- Compare generated artifact sets in parity job.

### Artifact Layout (Implemented)
- `artifacts/parity/<test>.snap` (generated per OS job; compared cross-job in parity stage)

### Files
- `.github/workflows/ci.yml`
- `tests/tools/compare_snapshots.py`
- Replay/parity-producing test files under `NovaTerminal.Tests/ReplayTests/`

### Acceptance Criteria
- Parity job fails when runtime output differs across OSes.
- Parity job is independent of checked-in `.snap` fixture copies.

### Tests
- CI dry run on PR branch to validate artifact upload/download/compare chain.

---

## Workstream E - Nightly Stress Correction (P0)

### Goal
Ensure nightly workflow executes real stress coverage.

### Current Issue
- `Category=ReplayStress` currently matches zero tests.

### Implementation Options
- Option A: Add real `ReplayStress` tests and keep filter.
- Option B: Change nightly filter to existing categories (`Stress`, `Performance`, `Latency`, optional `Regression` subset).

### Files
- `.github/workflows/nightly.yml`
- Optional: relevant test files in `NovaTerminal.Tests/`

### Acceptance Criteria
- Nightly runs non-zero stress/performance tests on all matrix OSes.
- Nightly failure signal maps to meaningful regressions.

---

## PR Sequence (Execution Order)

### PR1 (P0)
- Workstream A + Workstream B
- Security and secret consistency first.

### PR2 (P0/P1)
- Workstream C (path migration + compatibility handling).

### PR3 (P0)
- Workstream D + Workstream E (CI parity and nightly correctness).

### PR4 (Documentation Sync)
- Update roadmap/checklist docs to reflect new gates and completed hardening work.

---

## Definition of Done (Global)

- All existing tests pass.
- New/updated tests added for each changed behavior.
- CI pipelines updated and green.
- No new writes required in install directory.
- No automatic password injection path remains.
- Parity compare uses generated runtime artifacts.

---

## Risk Register

1. Legacy credential lookup regression  
Mitigation: canonical + fallback read chain with migration tests.

2. Data location migration bugs  
Mitigation: one-time migration with non-destructive copy semantics; test both clean and upgraded installs.

3. CI runtime increase  
Mitigation: keep parity artifact set minimal and deterministic; separate stress lanes from main PR gate if required.

4. Flaky performance thresholds  
Mitigation: isolate stress/perf assertions to nightly or dedicated perf jobs, not default unit lane.

---

## Verification Commands

```powershell
dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release
dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "Category=Replay"
dotnet test NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "Category=Stress|Category=Performance|Category=Latency"
```

---

## Rollback Strategy

- Keep changes split by PR sequence; revert per PR if needed.
- For path migration, preserve source files until successful first-load validation.
- For vault key migration, retain legacy fallback read for at least one release cycle.
