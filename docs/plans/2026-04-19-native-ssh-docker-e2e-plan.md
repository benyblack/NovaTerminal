# Native SSH Docker End-to-End Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a CI-capable live native SSH verification lane that connects to a Dockerized OpenSSH server, completes host key and password auth, runs a command, and verifies prompt return through the real `NativeSshSession` path.

**Architecture:** Keep this lane separate from deterministic transcript/replay coverage. Add a Docker fixture helper plus a live native SSH test that uses `NativeSshSession`, `AnsiParser`, and `TerminalBuffer` directly. Gate the lane so it runs only when Docker is available or explicitly enabled.

**Tech Stack:** C#, .NET 10, xUnit, Docker, OpenSSH container, native SSH session/interactions

---

### Task 1: Add a Docker fixture seam for a local OpenSSH container

**Files:**
- Create: `tests/NovaTerminal.ExternalSuites/NativeSsh/DockerSshFixture.cs`
- Modify: `tests/NovaTerminal.ExternalSuites/README.md`

**Step 1: Write the failing test or harness probe**

Add a small fixture-facing test helper or probe method that:
- starts a Docker container with OpenSSH
- maps the SSH port to localhost
- polls until the server is reachable
- exposes host, port, username, and password

**Step 2: Run it to verify failure**

Run:

```bash
dotnet build tests/NovaTerminal.ExternalSuites/NovaTerminal.ExternalSuites.csproj -c Release /nodeReuse:false
```

Expected: no runnable fixture exists yet, so the helper is still missing.

**Step 3: Write minimal implementation**

Implement:
- fixed container image choice
- fixed credentials
- deterministic prompt setup if needed
- startup and teardown logic
- Docker availability probe

Prefer one clearly named disposable helper rather than scattered shell commands in tests.

**Step 4: Run build again**

Run the same build command and confirm the fixture code compiles.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.ExternalSuites/NativeSsh/DockerSshFixture.cs tests/NovaTerminal.ExternalSuites/README.md
git commit -m "Add Docker SSH fixture for native end-to-end tests"
```

### Task 2: Add a live native SSH interaction handler for test auth flow

**Files:**
- Create: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshTestInteractionHandler.cs`

**Step 1: Write the failing test seam**

Add a small interaction handler suitable for tests that:
- accepts the first host key prompt
- returns the configured password
- fails clearly on unexpected prompt kinds

**Step 2: Run targeted build or test to verify failure**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSsh"
```

Expected: the helper does not exist yet.

**Step 3: Write minimal implementation**

Keep it explicit and deterministic. Do not make it generic production code.

**Step 4: Run targeted tests again**

Run the same command and confirm the new helper compiles cleanly with the native SSH test project.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Core.Tests/Ssh/NativeSshTestInteractionHandler.cs
git commit -m "Add native SSH test interaction handler"
```

### Task 3: Add the first live Docker end-to-end native SSH test

**Files:**
- Create: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`
- Modify: `tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj` if any package/reference/category metadata is needed

**Step 1: Write the failing test**

Add a test that:
- starts the Docker SSH fixture
- creates a native SSH profile pointing to `127.0.0.1:<mapped-port>`
- creates `NativeSshSession` with the test interaction handler
- attaches session output to `AnsiParser` + `TerminalBuffer`
- waits for prompt readiness
- sends `printf 'hello\n'`
- waits for `hello` and prompt return
- asserts final buffer state

**Step 2: Run the test to verify failure**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshDockerE2eTests" /nodeReuse:false
```

Expected: FAIL because the live lane is not wired yet or the fixture is incomplete.

**Step 3: Write minimal implementation**

Keep the first live assertion narrow:
- host key accepted
- password auth works
- command output appears
- prompt returns

Do not add fullscreen or resize behavior in this task.

**Step 4: Run the test to verify pass**

Run the same command and confirm the live Docker test passes when Docker is available.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj
git commit -m "Add live Docker native SSH end-to-end test"
```

### Task 4: Add clean gating for Docker-capable CI

**Files:**
- Modify: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`
- Modify: CI configuration only if needed later

**Step 1: Write the failing gating expectation**

Add a guard strategy that:
- runs when Docker is available
- skips clearly when Docker is unavailable

If needed, make the test require an explicit environment variable such as `NOVATERM_ENABLE_DOCKER_E2E=1`.

**Step 2: Verify current behavior**

Run the live test in an environment without the gate enabled and confirm the current behavior is too permissive or unclear.

**Step 3: Write minimal implementation**

Prefer an explicit skip path with a clear reason over hidden silent pass behavior.

**Step 4: Verify behavior**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshDockerE2eTests" /nodeReuse:false
```

Expected:
- PASS when Docker is available and the gate is enabled
- clean skip or clear no-op behavior when it is not

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs
git commit -m "Gate live Docker native SSH tests for CI"
```

### Task 5: Run focused verification and update docs

**Files:**
- Modify: `docs/native-ssh/Native_SSH_Test_Matrix.md`
- Modify: `docs/SSH_ROADMAP.md` if the lane changes roadmap status

**Step 1: Run focused core SSH verification**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh" /nodeReuse:false
```

Expected: PASS.

**Step 2: Run the live Docker end-to-end lane**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshDockerE2eTests" /nodeReuse:false
```

Expected: PASS when Docker is available and the gate is enabled.

**Step 3: Update docs**

Document:
- the new Docker E2E lane
- what it proves
- how it is gated
- what is still deferred to later live scenarios

**Step 4: Re-run the app replay/native SSH slice**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshReplayParityTests|FullyQualifiedName~Ssh" /nodeReuse:false
```

Expected: PASS.

**Step 5: Commit**

```bash
git add docs/native-ssh/Native_SSH_Test_Matrix.md docs/SSH_ROADMAP.md
git commit -m "Document Docker end-to-end native SSH coverage"
```

## Recommended Execution Order

1. Task 1
2. Task 2
3. Task 3
4. Task 4
5. Task 5

## Risks To Watch During Execution

- Do not let the Docker fixture depend on external internet or mutable third-party host state at runtime.
- Keep prompt assertions deterministic by forcing or detecting a simple shell prompt.
- Use condition-based waiting for prompt/output instead of arbitrary sleeps.
- Keep this first live lane narrow. Fullscreen and resize should be added only after the basic live lane is stable.

Plan complete and saved to `docs/plans/2026-04-19-native-ssh-docker-e2e-plan.md`. Two execution options:

**1. Subagent-Driven (this session)** - I dispatch fresh subagent per task, review between tasks, fast iteration

**2. Parallel Session (separate)** - Open new session with executing-plans, batch execution with checkpoints

Which approach?
