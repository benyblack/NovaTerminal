# Native SSH Dynamic Forwarding Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add native SSH dynamic port forwarding for direct-host native sessions using SOCKS5 `CONNECT`, while keeping the existing profile model and native `direct-tcpip` transport path.

**Architecture:** Keep SOCKS listener lifecycle in C# inside the native forwarding subsystem and reuse the Rust native SSH crate only for `direct-tcpip` channel open/read/write/close. This keeps the change additive, preserves terminal core boundaries, and avoids widening the FFI surface for v1.

**Tech Stack:** C#, Avalonia app/core projects, Rust `russh`, xUnit

---

### Task 1: Lock native forwarding scope with failing tests

**Files:**
- Modify: `d:\projects\nova2\tests\NovaTerminal.Core.Tests\Ssh\NativeSshSessionTests.cs`
- Modify: `d:\projects\nova2\tests\NovaTerminal.Core.Tests\Ssh\NativePortForwardSessionTests.cs`

**Step 1: Write the failing tests**

Add tests that prove:

- a native profile with one `Dynamic` forward no longer throws at session construction
- a SOCKS5 `CONNECT` request produces one `OpenDirectTcpIp` call with the requested host and port
- a native forward set containing `Local` and `Dynamic` forwards can start together

**Step 2: Run the tests to verify they fail**

Run:

```powershell
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshSessionTests|FullyQualifiedName~NativePortForwardSessionTests"
```

Expected:

- dynamic-forward native tests fail because non-local forwards are still rejected
- existing local-forward tests remain green

**Step 3: Commit**

```powershell
git add tests\NovaTerminal.Core.Tests\Ssh\NativeSshSessionTests.cs tests\NovaTerminal.Core.Tests\Ssh\NativePortForwardSessionTests.cs
git commit -m "test: capture native dynamic forwarding expectations"
```

### Task 2: Allow native sessions to accept dynamic forwards

**Files:**
- Modify: `d:\projects\nova2\src\NovaTerminal.Core\Ssh\Sessions\NativeSshSession.cs`

**Step 1: Write the minimal implementation**

Change native forward validation so:

- `Local` and `Dynamic` are accepted
- `Remote` remains rejected with an explicit message

Keep the rest of session construction unchanged.

**Step 2: Run the focused tests**

Run:

```powershell
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshSessionTests"
```

Expected:

- session acceptance tests pass
- SOCKS behavior tests still fail

**Step 3: Commit**

```powershell
git add src\NovaTerminal.Core\Ssh\Sessions\NativeSshSession.cs tests\NovaTerminal.Core.Tests\Ssh\NativeSshSessionTests.cs
git commit -m "feat: allow dynamic forwards in native ssh session setup"
```

### Task 3: Add SOCKS5 negotiation support to native forwarding

**Files:**
- Modify: `d:\projects\nova2\src\NovaTerminal.Core\Ssh\Native\NativePortForwardSession.cs`
- Modify: `d:\projects\nova2\tests\NovaTerminal.Core.Tests\Ssh\NativePortForwardSessionTests.cs`

**Step 1: Write the failing protocol tests**

Add deterministic tests for:

- SOCKS5 greeting with `NO AUTHENTICATION REQUIRED`
- `CONNECT` request with domain-name destination
- `CONNECT` request with IPv4 destination
- unsupported command returns a failure response and does not call `OpenDirectTcpIp`

Use real loopback sockets against the existing test fake interop so the tests exercise the actual listener path.

**Step 2: Run the tests to verify they fail**

Run:

```powershell
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativePortForwardSessionTests"
```

Expected:

- new SOCKS tests fail before implementation

**Step 3: Write the minimal implementation**

In `NativePortForwardSession`:

- split listener startup by `PortForwardKind`
- keep current local-forward flow intact
- add a dynamic accept loop that:
  - reads the SOCKS5 greeting
  - selects `NO AUTHENTICATION REQUIRED`
  - reads a single SOCKS request
  - validates `CONNECT`
  - extracts host/port
  - calls `_interop.OpenDirectTcpIp(...)`
  - sends success/failure reply
  - reuses the existing channel/socket pump model

Prefer small private helpers for:

- greeting read/write
- request parsing
- reply building
- dynamic channel setup

**Step 4: Run the tests to verify they pass**

Run:

```powershell
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativePortForwardSessionTests"
```

Expected:

- all local-forward and dynamic-forward tests pass

**Step 5: Commit**

```powershell
git add src\NovaTerminal.Core\Ssh\Native\NativePortForwardSession.cs tests\NovaTerminal.Core.Tests\Ssh\NativePortForwardSessionTests.cs
git commit -m "feat: add socks5 dynamic forwarding to native ssh"
```

### Task 4: Harden dynamic forwarding shutdown and mixed-forward behavior

**Files:**
- Modify: `d:\projects\nova2\src\NovaTerminal.Core\Ssh\Native\NativePortForwardSession.cs`
- Modify: `d:\projects\nova2\tests\NovaTerminal.Core.Tests\Ssh\NativePortForwardSessionTests.cs`

**Step 1: Add failing shutdown and coexistence tests**

Cover:

- disposing the native forward session closes dynamic listeners
- open dynamic channels are closed on dispose
- one `Local` and one `Dynamic` forward can coexist without interfering
- malformed SOCKS greeting/request is handled without tearing down unrelated forwards

**Step 2: Run the focused tests to verify they fail**

Run:

```powershell
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativePortForwardSessionTests"
```

**Step 3: Implement the hardening**

Keep the implementation additive:

- share channel cleanup between local and dynamic paths
- ensure client sockets are always disposed on protocol failure
- keep startup bind-failure policy unchanged

**Step 4: Run the tests to verify they pass**

Run:

```powershell
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativePortForwardSessionTests"
```

**Step 5: Commit**

```powershell
git add src\NovaTerminal.Core\Ssh\Native\NativePortForwardSession.cs tests\NovaTerminal.Core.Tests\Ssh\NativePortForwardSessionTests.cs
git commit -m "test: harden native dynamic forward teardown"
```

### Task 5: Verify Rust interop assumptions stay valid

**Files:**
- Review: `d:\projects\nova2\src\NovaTerminal.App\native\rusty_ssh\src\lib.rs`
- Modify only if needed: `d:\projects\nova2\src\NovaTerminal.App\native\rusty_ssh\src\lib.rs`

**Step 1: Run the existing native Rust tests**

Run:

```powershell
cargo test --manifest-path src\NovaTerminal.App\native\rusty_ssh\Cargo.toml --release
```

Expected:

- existing `direct-tcpip` coverage remains green without ABI changes

**Step 2: If Rust changes are required, add the narrowest failing test first**

Only touch `lib.rs` if the C# implementation exposes a real gap in:

- `direct-tcpip` open semantics
- channel close/eof ordering
- event routing

Keep v1 YAGNI: do not add Rust-side SOCKS parsing.

**Step 3: Commit**

```powershell
git add src\NovaTerminal.App\native\rusty_ssh\src\lib.rs
git commit -m "fix: align native ssh interop for dynamic forwarding"
```

Only make this commit if Rust code changed.

### Task 6: Update docs and record the follow-up gap

**Files:**
- Modify: `d:\projects\nova2\docs\native-ssh\Native_SSH_Test_Matrix.md`
- Modify: `d:\projects\nova2\docs\SSH_ROADMAP.md`

**Step 1: Update the manual matrix**

Add rows for:

- native dynamic forward through a direct host
- native dynamic forward through a one-hop jump host: follow-up / not yet supported

Make the current native forwarding scope explicit:

- local forward: supported
- dynamic forward: supported after this work
- remote forward: unsupported

**Step 2: Update roadmap notes**

Record:

- native dynamic forwarding is implemented for direct-host native sessions
- one-hop jump-host dynamic forwarding remains follow-up work

**Step 3: Commit**

```powershell
git add docs\native-ssh\Native_SSH_Test_Matrix.md docs\SSH_ROADMAP.md
git commit -m "docs: record native dynamic forwarding scope"
```

### Task 7: Full verification

**Files:**
- Verify only

**Step 1: Run core SSH tests**

```powershell
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh"
```

Expected:

- PASS for the full core SSH slice

**Step 2: Run app SSH tests**

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh"
```

Expected:

- PASS for the app SSH slice

**Step 3: Run Rust tests**

```powershell
cargo test --manifest-path src\NovaTerminal.App\native\rusty_ssh\Cargo.toml --release
```

Expected:

- PASS

**Step 4: Build the app**

```powershell
dotnet build src\NovaTerminal.App\NovaTerminal.App.csproj -c Release -p:SKIP_RUST_NATIVE_BUILD=1
```

Expected:

- PASS

**Step 5: Manual spot-check list**

Validate against a real endpoint:

- native direct-host dynamic forward works as a SOCKS5 proxy
- browser or CLI traffic traverses the proxy correctly
- invalid SOCKS client request fails cleanly
- native session shell remains usable while dynamic forward is active
- reconnect/close tears down the SOCKS listener

**Step 6: Final commit if verification/doc nits changed anything**

```powershell
git status --short
```

If any verification-driven adjustments were made, commit them with a focused message.

## Follow-Up After This Plan

Do not include in this implementation batch:

- native dynamic forwarding through one-hop jump hosts
- remote forwarding for native SSH
- SOCKS4 or SOCKS5 UDP/BIND support
