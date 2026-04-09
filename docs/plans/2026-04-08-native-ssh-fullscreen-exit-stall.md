# Native SSH Fullscreen Exit Stall Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Prevent native SSH fullscreen apps from stalling or flickering after aggressive resize churn by coalescing stale resize commands before issuing SSH `window-change` requests.

**Architecture:** Keep the fix native-backend-specific. Leave `TerminalView` resize throttling and VT/rendering untouched. Coalesce consecutive native resize commands in the Rust worker so only the latest dimensions are sent to the remote shell, and add regression tests around that queue behavior.

**Tech Stack:** C#, Rust, Avalonia, `russh`, xUnit

---

### Task 1: Pin the native-side resize intent in core tests

**Files:**
- Modify: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionTests.cs`

**Step 1: Write the failing test**

Add or extend a test that issues multiple `Resize(...)` calls on `NativeSshSession` and verifies the fake interop sees the latest dimensions as the effective intent for the session.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSshSessionTests"`

Expected: FAIL if the current test suite does not yet pin burst-resize intent clearly enough.

**Step 3: Write minimal implementation**

Only adjust the test scaffolding needed to express the burst-resize scenario. Do not change production code in this task unless absolutely required for the failing-test seam.

**Step 4: Run test to verify it passes**

Run the same command and confirm the targeted test passes.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Core.Tests/Ssh/NativeSshSessionTests.cs
git commit -m "Add native SSH burst resize intent test"
```

### Task 2: Add a Rust regression test for resize coalescing

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`

**Step 1: Write the failing test**

Add a Rust unit test around the worker-command handling that enqueues several consecutive resize commands and asserts only the last dimensions are applied to the channel-facing resize path.

**Step 2: Run test to verify it fails**

Run: `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release resize`

Expected: FAIL because the worker currently processes each resize individually.

**Step 3: Write minimal implementation**

Add the smallest helper/test seam necessary to observe coalesced resize handling without spinning up a full SSH connection.

**Step 4: Run test to verify it passes**

Run the same command and confirm the new resize test passes.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs
git commit -m "Add native SSH resize coalescing regression test"
```

### Task 3: Coalesce consecutive native resize commands in Rust

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`

**Step 1: Write the failing test**

If Task 2 used only a narrow helper, add one more failing assertion proving unrelated commands are not swallowed when draining resizes.

**Step 2: Run test to verify it fails**

Run: `cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release resize`

Expected: FAIL because the worker still forwards stale intermediate dimensions or does not preserve command ordering around non-resize commands.

**Step 3: Write minimal implementation**

Update the Rust worker loop so:
- when a `Resize` command is received, it drains immediately pending consecutive `Resize` commands
- it keeps only the latest `(cols, rows)`
- it issues a single `channel.window_change(...)`
- it does not skip or reorder non-resize commands

Keep the change local to native worker command handling.

**Step 4: Run test to verify it passes**

Run the same command and confirm the resize tests pass.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs
git commit -m "Coalesce native SSH resize commands"
```

### Task 4: Run focused native SSH verification

**Files:**
- No new product files

**Step 1: Run core SSH verification**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh"
```

Expected: PASS

**Step 2: Run Rust native verification**

Run:

```bash
cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml --release
```

Expected: PASS

**Step 3: Run app SSH slice**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~Ssh"
```

Expected: PASS

**Step 4: Manual verification**

Verify with native SSH:
- open `mc`
- resize aggressively for several seconds
- exit `mc`
- confirm the shell redraw does not stall and does not require another resize to recover

**Step 5: Commit**

```bash
git add .
git commit -m "Finalize native SSH fullscreen exit stall fix"
```
