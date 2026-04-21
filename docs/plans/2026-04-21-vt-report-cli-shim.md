# VT Report CLI Shim Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `NovaTerminal.exe --vt-report` visible from a shell by forwarding CLI-only VT report modes to a small console shim while preserving normal GUI startup behavior.

**Architecture:** Add a dedicated `NovaTerminal.Cli` console executable, move VT report command execution into a shared CLI layer, and have `NovaTerminal.App` forward `--vt-report` invocations to the shim. Keep `NovaTerminal.App` as `WinExe` and do not change any VT runtime logic.

**Tech Stack:** .NET 10, existing `NovaTerminal.App` startup path, xUnit, deterministic embedded JSON artifact

---

### Task 1: Introduce a shared VT report command layer

**Files:**
- Create: `src/NovaTerminal.App/Core/VtReportCommand.cs`
- Modify: `src/NovaTerminal.App/Core/VtReportCli.cs`
- Test: `tests/NovaTerminal.Tests/VtReportCliTests.cs`

**Step 1: Write the failing test**

Add tests that separate:
- CLI-mode detection from command execution
- execution of `--vt-report`
- execution of `--vt-report --json`

Add one test that asserts the shared command layer reports `--vt-report` as a handled CLI-only mode.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: FAIL because the shared command layer does not exist yet.

**Step 3: Write minimal implementation**

Create a small shared command abstraction that:
- inspects args
- reports whether the args are a supported CLI-only VT report command
- executes the VT report command against `TextWriter` instances

Keep the existing output format unchanged.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/VtReportCommand.cs src/NovaTerminal.App/Core/VtReportCli.cs tests/NovaTerminal.Tests/VtReportCliTests.cs
git commit -m "refactor: extract vt report command layer"
```

### Task 2: Add the console shim project

**Files:**
- Create: `src/NovaTerminal.Cli/NovaTerminal.Cli.csproj`
- Create: `src/NovaTerminal.Cli/Program.cs`
- Modify: `NovaTerminal.sln`
- Test: `tests/NovaTerminal.Tests/VtReportCliTests.cs`

**Step 1: Write the failing test**

Add a test that invokes the shared CLI command path through a console-oriented entry point contract and expects visible summary output.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: FAIL because the console shim project and entry point do not exist.

**Step 3: Write minimal implementation**

Create `NovaTerminal.Cli` as a normal `Exe` project that:
- references the shared VT report command layer
- calls it from `Main`
- exits with the returned code

Add the new project to the solution.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.Cli/NovaTerminal.Cli.csproj src/NovaTerminal.Cli/Program.cs NovaTerminal.sln tests/NovaTerminal.Tests/VtReportCliTests.cs
git commit -m "feat: add novaterminal cli shim"
```

### Task 3: Forward CLI-only modes from the GUI app

**Files:**
- Modify: `src/NovaTerminal.App/Program.cs`
- Create: `src/NovaTerminal.App/Core/CliShimLauncher.cs`
- Test: `tests/NovaTerminal.Tests/VtReportCliTests.cs`

**Step 1: Write the failing test**

Add tests that verify:
- GUI startup does not forward when no CLI-only mode is present
- `--vt-report` resolves a sibling CLI shim path
- the launcher propagates the child process exit code

Use a small injectable process-launch abstraction rather than launching a real child process in the unit test.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: FAIL because no forwarding launcher exists.

**Step 3: Write minimal implementation**

Implement a small launcher that:
- resolves `NovaTerminal.Cli.exe` from the app directory on Windows
- resolves the platform-appropriate sibling name on other platforms if needed
- starts the child process with the original args
- waits for completion
- returns the child exit code

Update `Program.Main` to use this only for recognized CLI-only VT report args.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Program.cs src/NovaTerminal.App/Core/CliShimLauncher.cs tests/NovaTerminal.Tests/VtReportCliTests.cs
git commit -m "feat: forward vt report mode to cli shim"
```

### Task 4: Ensure the shim is copied into app output and publish artifacts

**Files:**
- Modify: `src/NovaTerminal.App/NovaTerminal.App.csproj`
- Modify: `src/NovaTerminal.Cli/NovaTerminal.Cli.csproj`
- Test: `tests/NovaTerminal.Tests/VtReportCliTests.cs`

**Step 1: Write the failing test**

Add a test or verification helper that asserts the app-side launcher points at the expected sibling CLI executable name/path.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: FAIL because the project output/publish wiring is incomplete.

**Step 3: Write minimal implementation**

Update the build so the CLI shim is present beside the GUI app output. Keep the change narrow:
- project reference or explicit copy step
- no broad publish refactor

Ensure local `dotnet build` and publish outputs both contain the shim where the launcher expects it.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/NovaTerminal.App.csproj src/NovaTerminal.Cli/NovaTerminal.Cli.csproj tests/NovaTerminal.Tests/VtReportCliTests.cs
git commit -m "build: ship cli shim with app output"
```

### Task 5: Add direct executable verification and docs

**Files:**
- Modify: `tests/NovaTerminal.Tests/VtReportCliTests.cs`
- Modify: `docs/ghostty-gaps/vt_conformance_tooling.md`

**Step 1: Write the failing test**

Add a focused integration-style test that verifies the command path semantics expected by the user:
- summary mode remains available
- JSON mode remains available
- artifact parity test still passes

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: FAIL if any command-path assumptions changed while introducing the shim.

**Step 3: Write minimal implementation**

Document:
- that `NovaTerminal.exe --vt-report` is forwarded to the console shim
- that `NovaTerminal.Cli` is an implementation detail
- that the embedded artifact remains the source for emitted report bytes

Keep user-facing docs centered on `NovaTerminal.exe --vt-report`.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: PASS

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/VtReportCliTests.cs docs/ghostty-gaps/vt_conformance_tooling.md
git commit -m "docs: describe vt report cli shim behavior"
```

### Task 6: Final verification

**Files:**
- Modify: none unless verification exposes a bug

**Step 1: Run focused test suite**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~VtReportCliTests`

Expected: PASS

**Step 2: Run conformance drift check**

Run: `dotnet run --project src/NovaTerminal.Conformance/NovaTerminal.Conformance.csproj -c Release -- --validate --check-report src/NovaTerminal.App/Resources/vt-conformance-report.json`

Expected: `Rows: ... errors: 0; warnings: 0.` and report matches.

**Step 3: Run direct console shim check**

Run: `dotnet run --project src/NovaTerminal.Cli/NovaTerminal.Cli.csproj -c Debug -- --vt-report`

Expected: visible VT report summary in the shell.

**Step 4: Run preserved public command check**

Run: `dotnet run --project src/NovaTerminal.App/NovaTerminal.App.csproj -c Debug -- --vt-report`

Expected: visible VT report summary in the shell via GUI-to-CLI forwarding.

**Step 5: Commit**

```bash
git add .
git commit -m "test: verify vt report cli shim flow"
```
