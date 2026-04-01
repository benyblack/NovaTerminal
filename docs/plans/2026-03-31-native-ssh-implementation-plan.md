# Native SSH Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add an opt-in native SSH backend to NovaTerminal while preserving the current OpenSSH path and keeping terminal rendering untouched.

**Architecture:** Keep OpenSSH and native SSH as separate backends behind a small factory. Leave `OpenSshConfigCompiler`, `SshLaunchPlanner`, and `SshArgBuilder` OpenSSH-only. Keep native SSH session logic in core/session layers, keep Avalonia dialogs in app/services layers, and use a narrow poll-based Rust FFI surface under the existing app native build flow.

**Tech Stack:** C#/.NET 10, Avalonia 11, xUnit/Avalonia.Headless, Rust `cdylib`, existing `RustPtySession` packaging pattern, existing SSH profile store/session restore infrastructure.

---

## Repo-Specific Decisions

- Do not create a new top-level `native/` tree. Put the new crate at `src/NovaTerminal.App/native/rusty_ssh/` and build/copy it alongside the existing `rusty_pty` crate.
- Do not push native SSH through `RustPtySession`. `NativeSshSession` should implement `ITerminalSession` directly.
- Do not put Avalonia dialog types into `NovaTerminal.Core`. Core should expose small interaction request/response contracts; App should implement them.
- Do not change VT parsing or rendering code unless a bug proves it is necessary.

### Task 1: Backend Split Foundation (PR1)

**Files:**
- Create: `src/NovaTerminal.Core/Ssh/Models/SshBackendKind.cs`
- Create: `src/NovaTerminal.Core/Ssh/Sessions/ISshSessionFactory.cs`
- Create: `src/NovaTerminal.Core/Ssh/Sessions/SshSessionFactory.cs`
- Create: `src/NovaTerminal.Core/Ssh/Sessions/OpenSshSession.cs`
- Create: `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- Create: `src/NovaTerminal.Core/Ssh/Transport/IRemoteTerminalTransport.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Models/SshProfile.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Storage/JsonSshProfileStore.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Storage/SshJsonContext.cs`
- Modify: `src/NovaTerminal.App/Core/TerminalProfile.cs`
- Modify: `src/NovaTerminal.App/Services/Ssh/SshConnectionService.cs`
- Modify: `src/NovaTerminal.App/ViewModels/Ssh/NewSshConnectionViewModel.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/SshSessionFactoryTests.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/JsonSshProfileStoreTests.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/SshConnectionServiceTests.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs`

**Step 1: Write failing tests for backend persistence and factory routing**

Add tests that assert:
- `SshProfile` defaults `BackendKind` to `OpenSsh`.
- JSON store round-trips `BackendKind`.
- `SshConnectionService` projects backend selection into runtime `TerminalProfile`.
- `SshSessionFactory` returns `OpenSshSession` for `OpenSsh` and a stub `NativeSshSession` for `Native`.

**Step 2: Run tests to verify failure**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~JsonSshProfileStoreTests"`
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SshSessionFactoryTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshConnectionServiceTests|FullyQualifiedName~NewSshConnectionViewModelTests"`

Expected: FAIL because backend selection does not exist yet.

**Step 3: Implement minimal backend split without behavior change**

Implement:
- `SshBackendKind` with `OpenSsh = 0`, `Native = 1`.
- `SshProfile.BackendKind` defaulting to `OpenSsh`.
- Matching runtime field on `TerminalProfile` so the app can carry backend choice after profile projection.
- `OpenSshSession` as the current `SshSession` behavior moved behind a dedicated class.
- `NativeSshSession` stub that throws `NotSupportedException("Native SSH backend is not implemented yet.")`.
- `SshSessionFactory` that chooses backend by profile, but still preserves current OpenSSH behavior.
- `TerminalPane.InitializeSession` routing SSH session creation through `SshSessionFactory` instead of directly constructing `SshSession`.
- Keep `OpenSshConfigCompiler`, `SshLaunchPlanner`, and `SshArgBuilder` untouched and OpenSSH-only.

**Step 4: Run tests to verify pass**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~JsonSshProfileStoreTests|FullyQualifiedName~SshSessionFactoryTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshConnectionServiceTests|FullyQualifiedName~NewSshConnectionViewModelTests"`

Expected: PASS and existing SSH launch behavior remains OpenSSH-backed.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Models/SshBackendKind.cs \
        src/NovaTerminal.Core/Ssh/Sessions/ISshSessionFactory.cs \
        src/NovaTerminal.Core/Ssh/Sessions/SshSessionFactory.cs \
        src/NovaTerminal.Core/Ssh/Sessions/OpenSshSession.cs \
        src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs \
        src/NovaTerminal.Core/Ssh/Transport/IRemoteTerminalTransport.cs \
        src/NovaTerminal.Core/Ssh/Models/SshProfile.cs \
        src/NovaTerminal.Core/Ssh/Storage/JsonSshProfileStore.cs \
        src/NovaTerminal.Core/Ssh/Storage/SshJsonContext.cs \
        src/NovaTerminal.App/Core/TerminalProfile.cs \
        src/NovaTerminal.App/Services/Ssh/SshConnectionService.cs \
        src/NovaTerminal.App/ViewModels/Ssh/NewSshConnectionViewModel.cs \
        src/NovaTerminal.App/Controls/TerminalPane.axaml.cs \
        tests/NovaTerminal.Core.Tests/Ssh/SshSessionFactoryTests.cs \
        tests/NovaTerminal.Core.Tests/Ssh/JsonSshProfileStoreTests.cs \
        tests/NovaTerminal.Tests/Ssh/SshConnectionServiceTests.cs \
        tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs
git commit -m "Split SSH backends behind a factory"
```

### Task 2: Native Rust SSH Spike (PR2)

**Files:**
- Create: `src/NovaTerminal.App/native/rusty_ssh/Cargo.toml`
- Create: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`
- Create: `src/NovaTerminal.App/native/rusty_ssh/examples/smoke.rs`
- Modify: `src/NovaTerminal.App/NovaTerminal.App.csproj`
- Test: `src/NovaTerminal.App/native/rusty_ssh/tests/ffi_contract.rs`

**Step 1: Write failing Rust tests for the poll/event contract**

Add Rust tests that assert:
- a session handle can be created and closed safely.
- poll returns stable event ordering.
- resize/input/close requests validate null or invalid handles cleanly.
- event payload structs stay fixed-width and ABI-safe.

**Step 2: Run tests to verify failure**

Run:
- `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release`

Expected: FAIL because the crate and ABI do not exist yet.

**Step 3: Implement the minimal native SSH crate and packaging**

Implement:
- A new additive crate at `src/NovaTerminal.App/native/rusty_ssh/`.
- C ABI functions for:
  - create/connect
  - poll next event
  - write stdin bytes
  - resize PTY
  - provide auth/host-key response
  - close/free handle
- Poll/event model that covers:
  - stdout/stderr bytes
  - host key prompt
  - changed host key warning
  - password prompt
  - passphrase prompt
  - keyboard-interactive prompt
  - connected
  - exit status
  - disconnected/error
- Minimal interactive shell flow only.
- App project build/copy targets so `rusty_ssh` is built and copied next to `rusty_pty`.

**Step 4: Run tests and smoke harness**

Run:
- `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release`
- `cargo run --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release --example smoke -- --help`

Expected: tests PASS and smoke harness prints usage/help without crashing.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/Cargo.toml \
        src/NovaTerminal.App/native/rusty_ssh/src/lib.rs \
        src/NovaTerminal.App/native/rusty_ssh/examples/smoke.rs \
        src/NovaTerminal.App/native/rusty_ssh/tests/ffi_contract.rs \
        src/NovaTerminal.App/NovaTerminal.App.csproj
git commit -m "Add native SSH Rust spike with poll-based ABI"
```

### Task 3: Native SSH Session Wrapper (PR3)

**Files:**
- Create: `src/NovaTerminal.Core/Ssh/Native/INativeSshInterop.cs`
- Create: `src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs`
- Create: `src/NovaTerminal.Core/Ssh/Native/NativeSshEvent.cs`
- Create: `src/NovaTerminal.Core/Ssh/Native/NativeSshConnectionOptions.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/SshSessionFactory.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionTests.cs`

**Step 1: Write failing tests for the C# wrapper session lifecycle**

Add tests that assert:
- stdout bytes are decoded incrementally and emitted through `OnOutputReceived`.
- `SendInput` forwards bytes through interop.
- `Resize` forwards terminal dimensions.
- exit/disconnect events trigger one `OnExit`.
- dispose shuts down the poll loop cleanly.

Use a fake `INativeSshInterop` in tests; do not depend on live Rust/DLL loading for unit coverage.

**Step 2: Run tests to verify failure**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshSessionTests"`

Expected: FAIL because the wrapper session and interop abstraction do not exist yet.

**Step 3: Implement the wrapper with a background poll loop**

Implement:
- `NativeSshInterop` with `DllImport`/`LibraryImport` bindings to `rusty_ssh`.
- `NativeSshSession` with:
  - background poll loop
  - stateful UTF-8 decoding
  - input/resize forwarding
  - clean cancellation and close
  - no dependency on `RustPtySession`
- `SshSessionFactory` creating `NativeSshSession` when `BackendKind == Native`.

**Step 4: Run tests to verify pass**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshSessionTests|FullyQualifiedName~SshSessionFactoryTests"`

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Native/INativeSshInterop.cs \
        src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs \
        src/NovaTerminal.Core/Ssh/Native/NativeSshEvent.cs \
        src/NovaTerminal.Core/Ssh/Native/NativeSshConnectionOptions.cs \
        src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs \
        src/NovaTerminal.Core/Ssh/Sessions/SshSessionFactory.cs \
        tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionTests.cs
git commit -m "Wrap native SSH crate behind NativeSshSession"
```

### Task 4: SSH Interaction UX Service (PR4)

**Files:**
- Create: `src/NovaTerminal.Core/Ssh/Interactions/SshInteractionKind.cs`
- Create: `src/NovaTerminal.Core/Ssh/Interactions/SshInteractionRequest.cs`
- Create: `src/NovaTerminal.Core/Ssh/Interactions/SshInteractionResponse.cs`
- Create: `src/NovaTerminal.Core/Ssh/Interactions/ISshInteractionHandler.cs`
- Create: `src/NovaTerminal.App/Services/Ssh/ISshInteractionService.cs`
- Create: `src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs`
- Create: `src/NovaTerminal.App/ViewModels/Ssh/HostKeyPromptViewModel.cs`
- Create: `src/NovaTerminal.App/ViewModels/Ssh/AuthPromptViewModel.cs`
- Create: `src/NovaTerminal.App/Views/Ssh/HostKeyPromptDialog.axaml`
- Create: `src/NovaTerminal.App/Views/Ssh/HostKeyPromptDialog.axaml.cs`
- Create: `src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml`
- Create: `src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionInteractionTests.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs`

**Step 1: Write failing tests for request/response prompting**

Add tests that assert:
- native session pauses poll progression while waiting for host-key/auth responses.
- cancellation returns a deterministic reject/cancel response.
- App interaction service maps host key, password, passphrase, and keyboard-interactive requests to dialogs/view models.

**Step 2: Run tests to verify failure**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshSessionInteractionTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshInteractionServiceTests"`

Expected: FAIL because the interaction contracts and service do not exist yet.

**Step 3: Implement core interaction contracts and Avalonia service**

Implement:
- Core interaction DTOs/interfaces so `NativeSshSession` stays UI-agnostic.
- Avalonia interaction service that shows dialogs for:
  - unknown host key
  - changed host key
  - password auth
  - key passphrase
  - keyboard-interactive prompts
- Main window wiring so sessions can access the service through composition, not static UI calls.
- No terminal text scraping for auth/trust flows.

**Step 4: Run tests to verify pass**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshSessionInteractionTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~SshInteractionServiceTests"`

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Interactions/SshInteractionKind.cs \
        src/NovaTerminal.Core/Ssh/Interactions/SshInteractionRequest.cs \
        src/NovaTerminal.Core/Ssh/Interactions/SshInteractionResponse.cs \
        src/NovaTerminal.Core/Ssh/Interactions/ISshInteractionHandler.cs \
        src/NovaTerminal.App/Services/Ssh/ISshInteractionService.cs \
        src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs \
        src/NovaTerminal.App/ViewModels/Ssh/HostKeyPromptViewModel.cs \
        src/NovaTerminal.App/ViewModels/Ssh/AuthPromptViewModel.cs \
        src/NovaTerminal.App/Views/Ssh/HostKeyPromptDialog.axaml \
        src/NovaTerminal.App/Views/Ssh/HostKeyPromptDialog.axaml.cs \
        src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml \
        src/NovaTerminal.App/Views/Ssh/AuthPromptDialog.axaml.cs \
        src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs \
        src/NovaTerminal.App/Controls/TerminalPane.axaml.cs \
        src/NovaTerminal.App/MainWindow.axaml.cs \
        tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionInteractionTests.cs \
        tests/NovaTerminal.Tests/Ssh/SshInteractionServiceTests.cs
git commit -m "Add native SSH interaction service and dialogs"
```

### Task 5: Known Hosts And Backend-Safe Persistence (PR5)

**Files:**
- Create: `src/NovaTerminal.Core/Ssh/Native/KnownHostEntry.cs`
- Create: `src/NovaTerminal.Core/Ssh/Native/HostKeyFingerprintFormatter.cs`
- Create: `src/NovaTerminal.Core/Ssh/Native/NativeKnownHostsStore.cs`
- Modify: `src/NovaTerminal.App/Core/AppPaths.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Storage/JsonSshProfileStore.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Storage/SshJsonContext.cs`
- Modify: `src/NovaTerminal.App/Core/SessionManager.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeKnownHostsStoreTests.cs`
- Test: `tests/NovaTerminal.Tests/Core/AppPathsTests.cs`

**Step 1: Write failing tests for trust persistence and restore behavior**

Add tests that assert:
- first trust persists a host key entry.
- reconnect to same key passes lookup.
- changed key is flagged as mismatch.
- app paths expose a stable native known-hosts location.
- restored SSH sessions preserve backend selection through store-backed profile resolution.

**Step 2: Run tests to verify failure**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeKnownHostsStoreTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~AppPathsTests|FullyQualifiedName~SessionManager"`

Expected: FAIL because native known-hosts storage does not exist yet.

**Step 3: Implement native known-hosts storage and backend-safe restore**

Implement:
- App-owned known-hosts file path under `AppPaths`, for example `AppPaths.RootDirectory/ssh/native_known_hosts.json`.
- Deterministic fingerprint formatting helpers.
- Lookup/add/mismatch logic in `NativeKnownHostsStore`.
- Keep profile/backend restore store-backed; only change session restore schema if a test proves store lookup is insufficient.
- Do not store secrets in the known-hosts file.

**Step 4: Run tests to verify pass**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeKnownHostsStoreTests|FullyQualifiedName~JsonSshProfileStoreTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~AppPathsTests"`

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Native/KnownHostEntry.cs \
        src/NovaTerminal.Core/Ssh/Native/HostKeyFingerprintFormatter.cs \
        src/NovaTerminal.Core/Ssh/Native/NativeKnownHostsStore.cs \
        src/NovaTerminal.App/Core/AppPaths.cs \
        src/NovaTerminal.Core/Ssh/Storage/JsonSshProfileStore.cs \
        src/NovaTerminal.Core/Ssh/Storage/SshJsonContext.cs \
        src/NovaTerminal.App/Core/SessionManager.cs \
        tests/NovaTerminal.Core.Tests/Ssh/NativeKnownHostsStoreTests.cs \
        tests/NovaTerminal.Tests/Core/AppPathsTests.cs
git commit -m "Persist native SSH host trust and backend restore state"
```

### Task 6: Local Port Forwarding Parity (PR6)

**Files:**
- Create: `src/NovaTerminal.Core/Ssh/Native/NativePortForwardSession.cs`
- Create: `src/NovaTerminal.Core/Ssh/Transport/PortForwardModels.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativePortForwardSessionTests.cs`

**Step 1: Write failing tests for forward lifecycle**

Add tests that assert:
- local forward listeners bind from `SshProfile.Forwards`.
- multiple forwards can coexist.
- listener/channel teardown occurs on session dispose.
- one bind failure is surfaced deterministically.

**Step 2: Run tests to verify failure**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativePortForwardSessionTests"`

Expected: FAIL because native forwarding orchestration does not exist yet.

**Step 3: Implement local forwarding only**

Implement:
- `NativePortForwardSession` for listener lifecycle and byte shuttling.
- Rust support for `direct-tcpip` or equivalent channel open path.
- Startup policy documented in code and logs:
  - either continue with partial success and warn
  - or fail whole session on any enabled bind failure
- Keep terminal shell usable while forwards are active.

**Step 4: Run tests to verify pass**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativePortForwardSessionTests"`
- `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release`

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Native/NativePortForwardSession.cs \
        src/NovaTerminal.Core/Ssh/Transport/PortForwardModels.cs \
        src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs \
        src/NovaTerminal.App/native/rusty_ssh/src/lib.rs \
        tests/NovaTerminal.Core.Tests/Ssh/NativePortForwardSessionTests.cs
git commit -m "Add native SSH local port forwarding"
```

### Task 7: Jump Host Support (PR7)

**Files:**
- Create: `src/NovaTerminal.Core/Ssh/Native/JumpHostConnectPlan.cs`
- Create: `src/NovaTerminal.Core/Ssh/Native/NativeJumpHostConnector.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/SshSessionFactory.cs`
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/JumpHostConnectPlanTests.cs`

**Step 1: Write failing tests for one-hop jump planning**

Add tests that assert:
- `SshProfile.JumpHops` becomes a one-hop native connect plan.
- multi-hop profiles fail with a clear unsupported result in this PR.
- factory/session logging makes the selected backend/path visible.

**Step 2: Run tests to verify failure**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~JumpHostConnectPlanTests"`

Expected: FAIL because native jump-host planning does not exist yet.

**Step 3: Implement one-hop jump host support**

Implement:
- explicit `JumpHostConnectPlan`.
- `NativeJumpHostConnector` for one-hop only.
- clear unsupported handling for multi-hop instead of hidden fallback.
- keep OpenSSH backend available as manual fallback.

**Step 4: Run tests to verify pass**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~JumpHostConnectPlanTests"`
- `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release`

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Native/JumpHostConnectPlan.cs \
        src/NovaTerminal.Core/Ssh/Native/NativeJumpHostConnector.cs \
        src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs \
        src/NovaTerminal.Core/Ssh/Sessions/SshSessionFactory.cs \
        src/NovaTerminal.App/native/rusty_ssh/src/lib.rs \
        tests/NovaTerminal.Core.Tests/Ssh/JumpHostConnectPlanTests.cs
git commit -m "Add one-hop jump host support for native SSH"
```

### Task 8: Hardening, Diagnostics, And Rollout Controls (PR8)

**Files:**
- Create: `src/NovaTerminal.Core/Ssh/Native/NativeSshMetrics.cs`
- Create: `src/NovaTerminal.Core/Ssh/Native/NativeSshFailureClassifier.cs`
- Modify: `src/NovaTerminal.App/Core/TerminalSettings.cs`
- Modify: `src/NovaTerminal.App/ViewModels/Ssh/NewSshConnectionViewModel.cs`
- Modify: `src/NovaTerminal.App/Views/Ssh/NewSshConnectionView.axaml`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/SshSessionFactory.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshFailureClassifierTests.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs`

**Step 1: Write failing tests for backend selection and rollout gating**

Add tests that assert:
- profile editor can store `OpenSsh` or `Native`.
- global experimental toggle can disable native session creation.
- failure classifier maps known native errors into stable buckets.

**Step 2: Run tests to verify failure**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshFailureClassifierTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~NewSshConnectionViewModelTests"`

Expected: FAIL because backend selector, gating, and classifier do not exist yet.

**Step 3: Implement conservative rollout controls**

Implement:
- per-profile backend selector in the SSH editor UI.
- global experimental setting in `TerminalSettings`.
- `SshSessionFactory` refusal path when native backend is disabled.
- metrics for:
  - connect latency
  - host key duration
  - auth duration
  - time to first output byte
  - disconnect reason
  - forward setup results
- failure classification buckets for timeout, auth, host key mismatch, channel open, forward bind, and remote disconnect.
- explicit user-visible path to switch the profile back to OpenSSH.

**Step 4: Run tests to verify pass**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshFailureClassifierTests|FullyQualifiedName~NativeSshSessionTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~NewSshConnectionViewModelTests|FullyQualifiedName~SshConnectionServiceTests"`

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Native/NativeSshMetrics.cs \
        src/NovaTerminal.Core/Ssh/Native/NativeSshFailureClassifier.cs \
        src/NovaTerminal.App/Core/TerminalSettings.cs \
        src/NovaTerminal.App/ViewModels/Ssh/NewSshConnectionViewModel.cs \
        src/NovaTerminal.App/Views/Ssh/NewSshConnectionView.axaml \
        src/NovaTerminal.App/Controls/TerminalPane.axaml.cs \
        src/NovaTerminal.App/MainWindow.axaml.cs \
        src/NovaTerminal.Core/Ssh/Sessions/SshSessionFactory.cs \
        src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs \
        tests/NovaTerminal.Core.Tests/Ssh/NativeSshFailureClassifierTests.cs \
        tests/NovaTerminal.Tests/Ssh/NewSshConnectionViewModelTests.cs
git commit -m "Add native SSH rollout controls and diagnostics"
```

### Task 9: Full Verification And Documentation

**Files:**
- Modify: `docs/SSH_ROADMAP.md`
- Create: `docs/native-ssh/Native_SSH_Test_Matrix.md`
- Verify only: `src/NovaTerminal.Core/Ssh/**`, `src/NovaTerminal.App/Views/Ssh/**`, `src/NovaTerminal.App/native/rusty_ssh/**`

**Step 1: Run focused .NET SSH suites**

Run:
- `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh"`

Expected: PASS.

**Step 2: Run native crate tests**

Run:
- `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release`

Expected: PASS.

**Step 3: Run broader app smoke**

Run:
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~PtySmoke|FullyQualifiedName~SessionAuthSurfaceTests"`

Expected: PASS and no password-injection API regressions.

**Step 4: Manual matrix**

Verify:
- OpenSSH backend still connects exactly as before.
- Native backend works for password, key, encrypted key, and keyboard-interactive auth.
- first trust, trusted reconnect, and changed host key paths behave correctly.
- native shell survives resize and fullscreen TUI usage.
- one local forward and one-hop jump host work.
- switching a broken native profile back to OpenSSH is obvious and safe.

**Step 5: Update docs and commit**

```bash
git add docs/SSH_ROADMAP.md docs/native-ssh/Native_SSH_Test_Matrix.md
git commit -m "Document native SSH verification matrix and rollout guidance"
```

## Recommended Execution Order

1. Task 1
2. Task 2
3. Task 3
4. Task 4
5. Task 5
6. Task 6
7. Task 7
8. Task 8
9. Task 9

## Risks To Watch During Execution

- The current app assumes all SSH sessions are OpenSSH-backed and shell-command based. Keep `TerminalPane` changes narrow.
- `src/NovaTerminal.App/native` currently builds one crate. Keep the new native SSH crate additive and do not break `rusty_pty`.
- Session restore is profile-store driven today. Verify whether backend persistence is fully covered by the profile store before expanding `NovaSession`.
- Avoid mixing host-key/auth UX into terminal output parsing. That would violate both the doc pack and the repo architecture rules.

Plan complete and saved to `docs/plans/2026-03-31-native-ssh-implementation-plan.md`. Two execution options:

**1. Subagent-Driven (this session)** - I dispatch fresh subagent per task, review between tasks, fast iteration

**2. Parallel Session (separate)** - Open new session with executing-plans, batch execution with checkpoints

Which approach?
