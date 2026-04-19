# Native SSH VT Correctness Hardening Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Restore native SSH VT correctness for fullscreen exit, resize stability, and post-command newline behavior without changing the terminal parser or renderer unless tests prove that is necessary.

**Architecture:** Treat [NativeSshSession.cs](/d:/projects/nova2/src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs) as the native-backend contract adapter. First pin the regressions with deterministic `INativeSshInterop`-driven tests and harden resize/output behavior there. Then add a separate end-to-end verification layer using the existing external-suite pattern so the real native event pipeline is covered without making Step 1 depend on live SSH.

**Tech Stack:** C#, .NET 10, xUnit, Avalonia, existing VT parser/buffer tests, native Rust `rusty_ssh` interop

---

### Task 1: Add failing native session tests for resize failure and buffer-level parity

**Files:**
- Modify: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionTests.cs`
- Create: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshTerminalParityTests.cs`

**Step 1: Write the failing tests**

Add tests that cover:
- resize failure from fake native interop does not escape `NativeSshSession.Resize(...)`
- chunked alternate-screen enter / exit sequences still produce the same `TerminalBuffer` behavior as the VT oracle
- post-command prompt / newline sequences do not introduce extra blank lines

For the parity tests, wire the session output into:

```csharp
var buffer = new TerminalBuffer(80, 24);
var parser = new AnsiParser(buffer);
session.OnOutputReceived += parser.Process;
```

Use a fake interop that can:
- enqueue `NativeSshEvent.Data(...)`
- optionally throw from `Resize(...)`
- keep returning later events after a resize failure

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshSessionTests|FullyQualifiedName~NativeSshTerminalParityTests"
```

Expected: FAIL because native SSH does not yet pin or satisfy the new non-fatal resize and parity expectations.

**Step 3: Write minimal test scaffolding**

Extend the existing fake interop in `NativeSshSessionTests.cs` or extract a tiny shared fake only if that is required to keep the new tests readable. Do not change production code in this task.

**Step 4: Run the targeted tests again**

Run the same command and confirm the new tests still fail for the expected production-code reasons, not due to broken test setup.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionTests.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSshTerminalParityTests.cs
git commit -m "Add native SSH VT correctness regression tests"
```

### Task 2: Make native resize non-fatal and preserve session usability

**Files:**
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`

**Step 1: Write one more failing assertion if needed**

If Task 1 did not already pin it tightly enough, add a focused failing assertion that:
- calls `Resize(...)`
- has fake interop throw
- then verifies later output events are still processed and `OnExit` still fires once

**Step 2: Run the resize-focused test to verify failure**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshSessionTests"
```

Expected: FAIL because resize exceptions still escape or poison the session path.

**Step 3: Write minimal implementation**

In `NativeSshSession.Resize(...)`:
- keep the current guard clauses for invalid handle and non-positive dimensions
- wrap `_interop.Resize(...)` in a native-backend-specific `try/catch`
- log the resize failure through `_log`
- do not rethrow
- do not mark the session exited
- keep recorder behavior unchanged

Do not change `TerminalView`, `TerminalPane`, or the VT parser in this task.

**Step 4: Run the resize-focused test to verify pass**

Run the same command and confirm the resize regression tests pass.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionTests.cs
git commit -m "Make native SSH resize failures non-fatal"
```

### Task 3: Harden native output parity for chunked fullscreen and prompt-return scenarios

**Files:**
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- Modify: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshTerminalParityTests.cs`

**Step 1: Run the parity tests to verify current failure**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshTerminalParityTests"
```

Expected: FAIL because native output delivery is not yet pinned strongly enough for chunked alternate-screen and post-command newline parity.

**Step 2: Write minimal implementation**

Only if the failing tests require it, adjust `NativeSshSession` output handling to preserve PTY-like session semantics:
- keep incremental UTF-8 decoding stateful across `Data` events
- preserve emission ordering
- do not drop late buffered output
- avoid introducing any newline normalization that differs from incoming terminal data

If the tests pass without production changes after Task 2, keep this task to test cleanup only and do not change production code unnecessarily.

**Step 3: Run the parity tests to verify pass**

Run the same command and confirm the parity tests pass.

**Step 4: Run the full native SSH core slice**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSshTerminalParityTests.cs
git commit -m "Harden native SSH output parity for VT correctness"
```

### Task 4: Verify the app-level VT oracle still agrees with native SSH behavior

**Files:**
- No new product files
- Reference only: `tests/NovaTerminal.Tests/AlternateScreenTests.cs`
- Reference only: `tests/NovaTerminal.Tests/Regressions/MidnightCommanderTests.cs`

**Step 1: Run alternate-screen and resize oracle tests**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~AlternateScreenTests|FullyQualifiedName~MidnightCommanderTests"
```

Expected: PASS.

**Step 2: Run the native SSH app/core verification slice**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh"
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSsh"
```

Expected: PASS.

**Step 3: Manual verification**

Verify in the app with native SSH only:
- connect to a remote shell
- open a fullscreen TUI such as `mc` or `vim`
- resize aggressively
- exit the TUI
- run a simple command like `printf 'a\n'`
- confirm the prompt returns without crash, blank-line buildup, or broken fullscreen exit state

**Step 4: Record open gaps**

Write down any remaining issues that only reproduce in the real native event pipeline and were not exposed by the Step 1 deterministic tests.

**Step 5: Commit**

```bash
git add .
git commit -m "Verify native SSH VT correctness hardening"
```

### Task 5: Build the Step 2 end-to-end native SSH verification harness

**Files:**
- Create: `tests/NovaTerminal.ExternalSuites/NativeSsh/NativeSshScenarioPlan.cs`
- Create: `tests/NovaTerminal.ExternalSuites/NativeSsh/NativeSshTranscriptDriver.cs`
- Create: `tests/NovaTerminal.ExternalSuites/tests/Replays/NativeSsh/.gitkeep`
- Modify: `tests/NovaTerminal.ExternalSuites/Program.cs`
- Modify: `tests/NovaTerminal.ExternalSuites/README.md`

**Step 1: Write the failing harness entry points**

Add a new `--suite native-ssh` path in `Program.cs` that currently throws or returns unsupported.

Add scenario placeholders for:
- `fullscreen-exit`
- `resize-burst`
- `prompt-return`

**Step 2: Run the harness to verify failure**

Run:

```bash
dotnet run --project tests/NovaTerminal.ExternalSuites/NovaTerminal.ExternalSuites.csproj -- --suite native-ssh --scenario fullscreen-exit --out tests/NovaTerminal.ExternalSuites/tests/Replays/NativeSsh/fullscreen-exit.rec
```

Expected: FAIL because the native-SSH external suite is not implemented yet.

**Step 3: Write minimal implementation**

Implement a harness that:
- follows the existing `vttest` adapter pattern
- drives scripted native SSH scenarios without depending on a live CI SSH server
- emits deterministic `.rec` recordings for later replay/assertion

Do not broaden this into general SSH automation. Keep it focused on the three correctness scenarios.

**Step 4: Run the harness again**

Run the same command and confirm a `.rec` file is generated successfully for at least one scenario.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.ExternalSuites/NativeSsh/NativeSshScenarioPlan.cs tests/NovaTerminal.ExternalSuites/NativeSsh/NativeSshTranscriptDriver.cs tests/NovaTerminal.ExternalSuites/tests/Replays/NativeSsh/.gitkeep tests/NovaTerminal.ExternalSuites/Program.cs tests/NovaTerminal.ExternalSuites/README.md
git commit -m "Add native SSH external verification harness"
```

### Task 6: Turn Step 2 recordings into replay-backed regression coverage

**Files:**
- Create: `tests/NovaTerminal.Tests/ReplayTests/NativeSshReplayParityTests.cs`
- Create: `tests/NovaTerminal.Tests/Fixtures/Replay/native_ssh_fullscreen_exit.snap`
- Create: `tests/NovaTerminal.Tests/Fixtures/Replay/native_ssh_prompt_return.snap`
- Modify: `tests/NovaTerminal.Tests/Fixtures/Replay/` via new `.rec` artifacts copied from the external suite

**Step 1: Write the failing replay tests**

Add replay tests that consume the Step 2 `.rec` artifacts and assert:
- fullscreen exit returns to a sane prompt state
- prompt-return output does not accumulate extra blank lines
- resize-burst replay does not leave the final buffer in a broken layout

**Step 2: Run the replay tests to verify failure**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshReplayParityTests"
```

Expected: FAIL until the new recordings and snapshots are checked in and validated.

**Step 3: Write minimal implementation**

Copy the generated `.rec` artifacts into `tests/NovaTerminal.Tests/Fixtures/Replay/`, capture the expected `.snap` files, and keep assertions focused on stable terminal end state rather than timing noise.

**Step 4: Run the replay tests to verify pass**

Run the same command and confirm the native SSH replay parity tests pass.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/ReplayTests/NativeSshReplayParityTests.cs tests/NovaTerminal.Tests/Fixtures/Replay/native_ssh_fullscreen_exit.rec tests/NovaTerminal.Tests/Fixtures/Replay/native_ssh_fullscreen_exit.snap tests/NovaTerminal.Tests/Fixtures/Replay/native_ssh_prompt_return.rec tests/NovaTerminal.Tests/Fixtures/Replay/native_ssh_prompt_return.snap
git commit -m "Add native SSH replay parity coverage"
```

## Recommended Execution Order

1. Task 1
2. Task 2
3. Task 3
4. Task 4
5. Task 5
6. Task 6

## Risks To Watch During Execution

- Do not normalize or rewrite terminal data in a way that diverges from the existing PTY/OpenSSH path.
- Do not change VT parsing or rendering unless a failing Step 1 test proves the session boundary is not the root cause.
- Keep native SSH-specific fixes out of generic resize or terminal UI code unless Step 1 evidence forces a wider change.
- Keep Step 2 deterministic. If it starts depending on a live SSH host, it will not be suitable for CI.

Plan complete and saved to `docs/plans/2026-04-19-native-ssh-vt-correctness-plan.md`. Two execution options:

**1. Subagent-Driven (this session)** - I dispatch fresh subagent per task, review between tasks, fast iteration

**2. Parallel Session (separate)** - Open new session with executing-plans, batch execution with checkpoints

Which approach?
