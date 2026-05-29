# Native SSH Vim Scroll Regression Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reproduce and fix the native SSH `vim` downward-scroll regression so scrolling with `scrolloff=5` updates the whole alternate-screen window correctly and the cursor can reach the end of the file.

**Architecture:** First add a deterministic failing regression that models the `vim` downward-scroll pattern and proves whether the VT core is actually wrong. Then add a live Docker `vim` scenario over native SSH. Only after the failing evidence is in place should production code change, with preference given to native SSH delivery/session behavior unless the deterministic repro proves the VT scroll path is at fault.

**Tech Stack:** C#, xUnit, NovaTerminal VT buffer/parser, Native SSH session, Dockerized OpenSSH fixture, `vim`

---

### Task 1: Document the Existing Native SSH and Vim Test Surface

**Files:**
- Inspect: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`
- Inspect: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshTerminalParityTests.cs`
- Inspect: `tests/NovaTerminal.Tests/ReplayTests/NativeSshReplayParityTests.cs`
- Inspect: `tests/NovaTerminal.Tests/AlternateScreenTests.cs`
- Inspect: `tests/NovaTerminal.Tests/Regressions/MidnightCommanderTests.cs`

**Step 1: Review the current coverage**

Read the files above and note what is already covered for alternate-screen, scrolling-region, and Dockerized native SSH behavior.

**Step 2: Record the missing case**

Write down the exact missing behavior: `vim` downward scroll with `scrolloff=5`, no resize required, stale window content until `Ctrl+L`.

**Step 3: No code changes**

Do not change production code in this task.

**Step 4: Commit**

No commit for this inspection-only task.

### Task 2: Add a Deterministic Failing Repro for Vim-Style Downward Scroll

**Files:**
- Modify: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshTerminalParityTests.cs`
- Reference: `src/NovaTerminal.VT/AnsiParser.cs`
- Reference: `src/NovaTerminal.VT/TerminalBuffer.WritePath.cs`
- Reference: `src/NovaTerminal.VT/TerminalBuffer.AccessAndSnapshot.cs`

**Step 1: Write the failing test**

Add a test that models a `vim`-style alternate-screen editing window with a downward scroll pattern equivalent to `scrolloff=5`.

The test should:

- enter alternate-screen
- seed visible rows representing a long file
- apply the control sequence pattern that scrolls the window downward
- assert the visible buffer advances across the full editing area

**Step 2: Run the new test to verify it fails**

Run:

```powershell
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshTerminalParityTests"
```

Expected: the new test fails and reveals whether the VT path already reproduces the bug.

**Step 3: Capture the failure shape**

Record whether:

- only the last line changes
- stale rows remain
- the cursor/content alignment is wrong

**Step 4: Commit**

Do not commit yet. Keep the failing test uncommitted until the fix path is understood.

### Task 3: Decide the Fix Boundary From the Deterministic Result

**Files:**
- Inspect: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshTerminalParityTests.cs`
- Inspect: `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- Inspect: `src/NovaTerminal.VT/AnsiParser.cs`
- Inspect: `src/NovaTerminal.VT/TerminalBuffer.WritePath.cs`

**Step 1: Analyze the failing result**

Use the deterministic test outcome to decide:

- if parser/buffer fails directly, the VT scroll path is the fix target
- if parser/buffer passes, native SSH delivery/live behavior is the fix target

**Step 2: Choose one fix boundary**

Write down the single chosen boundary before changing code.

**Step 3: No speculative edits**

Do not modify both native SSH delivery and VT core in the same step.

**Step 4: Commit**

No commit in this analysis task.

### Task 4: Add a Live Docker Vim Repro Test

**Files:**
- Modify: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`
- Modify: `tests/NovaTerminal.ExternalSuites/NativeSsh/Dockerfile` if `vim` is not available
- Reference: `tests/NovaTerminal.Core.Tests/Ssh/DockerSshFixture.cs`
- Reference: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshTestInteractionHandler.cs`

**Step 1: Write the failing live test**

Add a Docker E2E test that:

- starts the Docker SSH fixture
- creates a long file remotely
- launches `vim -u NONE -N`
- sets `scrolloff=5`
- sends repeated `j`
- asserts visible window progression and eventual end-of-file reachability

**Step 2: Run the live test to verify it fails**

Run:

```powershell
$env:NOVATERM_ENABLE_DOCKER_E2E='1'
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshDockerE2eTests"
```

Expected: the new vim scenario fails before the fix.

**Step 3: Keep the assertion outcome-based**

Avoid pixel-perfect snapshots. Assert:

- visible buffer changes during downward scroll
- the cursor can continue progressing
- the session reaches the actual end of the file

**Step 4: Commit**

Do not commit yet. The live test should stay part of the red-green cycle.

### Task 5: Implement the Minimal Fix at the Chosen Boundary

**Files:**
- Modify exactly one of:
  - `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
  - `src/NovaTerminal.VT/AnsiParser.cs`
  - `src/NovaTerminal.VT/TerminalBuffer.WritePath.cs`
  - `src/NovaTerminal.App/Core/TerminalDrawOperation.cs`

**Step 1: Write the smallest production change**

Implement only the minimal code required to make the deterministic and live vim tests pass.

**Step 2: Preserve architecture constraints**

- prefer additive changes over refactors
- keep UI concerns out of terminal core
- do not change unrelated resize/fullscreen logic

**Step 3: Run the previously failing deterministic test**

Run:

```powershell
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshTerminalParityTests"
```

Expected: the new vim regression test passes.

**Step 4: Run the previously failing live Docker test**

Run:

```powershell
$env:NOVATERM_ENABLE_DOCKER_E2E='1'
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshDockerE2eTests"
```

Expected: the new vim scenario passes.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs src/NovaTerminal.VT/AnsiParser.cs src/NovaTerminal.VT/TerminalBuffer.WritePath.cs src/NovaTerminal.App/Core/TerminalDrawOperation.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSshTerminalParityTests.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs tests/NovaTerminal.ExternalSuites/NativeSsh/Dockerfile
git commit -m "fix native ssh vim downward scroll regression"
```

Stage only the files actually touched.

### Task 6: Run the Native SSH Regression Matrix

**Files:**
- Verify only

**Step 1: Run the SSH core slice**

```powershell
$env:NOVATERM_ENABLE_DOCKER_E2E='1'
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh" /nodeReuse:false
```

Expected: all SSH-focused tests pass.

**Step 2: Run replay parity**

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshReplayParityTests" /nodeReuse:false
```

Expected: replay parity remains green.

**Step 3: Run alternate-screen / TUI regressions if the VT core was touched**

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~AlternateScreenTests|FullyQualifiedName~MidnightCommanderTests" /nodeReuse:false
```

Expected: existing alternate-screen coverage still passes.

**Step 4: Commit**

If verification changes were needed, commit them separately; otherwise no commit.

### Task 7: Update Documentation if the Repro Surface Changed

**Files:**
- Modify if needed: `tests/NovaTerminal.ExternalSuites/README.md`
- Modify if needed: `docs/native-ssh/Native_SSH_Test_Matrix.md`

**Step 1: Document any new live vim scenario**

If the Docker `vim` scenario adds a new runnable lane or prerequisite, document the command and gating.

**Step 2: Keep docs concise**

Do not duplicate existing README content unnecessarily.

**Step 3: Run no-op verification**

Open the docs and confirm commands and filenames are accurate.

**Step 4: Commit**

```bash
git add tests/NovaTerminal.ExternalSuites/README.md docs/native-ssh/Native_SSH_Test_Matrix.md
git commit -m "document native ssh vim scroll regression coverage"
```

Stage only changed docs.
