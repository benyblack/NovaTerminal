# Native SFTP Progress Callbacks Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add real live byte progress for NativeSSH transfers by streaming Rust SFTP copy progress into C# `TransferJob` updates.

**Architecture:** Keep the current NativeSSH one-shot transfer API, but extend it with a native callback function pointer and opaque user-data payload. Rust emits per-file byte progress during copy loops, `NativeSshInterop` marshals it into `NativeSftpTransferProgress`, and `SftpService` continues to own job updates and UI notifications.

**Tech Stack:** C#, .NET 10, Avalonia, P/Invoke, Rust, `russh`, `russh-sftp`, xUnit

---

### Task 1: Define The Managed Progress Contract

**Files:**
- Modify: `src/NovaTerminal.Core/Ssh/Native/NativeSftpTransferModels.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Native/INativeSshInterop.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSftpTransferInteropTests.cs`

**Step 1: Write the failing tests**

Add tests that describe the progress payload shape and callback forwarding contract:

```csharp
[Fact]
public void ProgressPayload_AllowsKnownTotalAndCurrentPath()
{
    var progress = new NativeSftpTransferProgress
    {
        BytesDone = 128,
        BytesTotal = 1024,
        CurrentPath = "/tmp/report.txt"
    };

    Assert.Equal(128, progress.BytesDone);
    Assert.Equal(1024, progress.BytesTotal);
    Assert.Equal("/tmp/report.txt", progress.CurrentPath);
}
```

Also add an interop-focused test scaffold that will fail until `NativeSshInterop` can translate a native callback into the managed `Action<NativeSftpTransferProgress>`.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~NativeSftpTransferInteropTests" --no-restore -m:1
```

Expected: FAIL because the callback translation test is not implemented yet.

**Step 3: Write minimal contract changes**

- Keep `NativeSftpTransferProgress` as the managed payload type
- Do not widen `INativeSshInterop.RunSftpTransfer(...)` beyond the existing `Action<NativeSftpTransferProgress>? progress`
- Add only the interop support types needed to marshal native callback data

**Step 4: Run test to verify the simple payload test passes and the callback test still drives the next task**

Run the same command.

Expected: one payload test PASS, callback translation test still FAIL.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Native/NativeSftpTransferModels.cs src/NovaTerminal.Core/Ssh/Native/INativeSshInterop.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSftpTransferInteropTests.cs
git commit -m "test: define native sftp progress contract"
```

### Task 2: Add Managed Native Callback Marshaling

**Files:**
- Modify: `src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSftpTransferInteropTests.cs`

**Step 1: Write the failing tests**

Add a focused test that proves native callback payloads become managed progress updates. Do this by extracting a small internal helper instead of trying to invoke the real DLL from the test:

```csharp
[Fact]
public void NativeProgressCallback_TranslatesPayloadToManagedProgress()
{
    NativeSftpTransferProgress? observed = null;

    NativeSshInterop.InvokeManagedProgressCallbackForTest(
        progress => observed = progress,
        bytesDone: 512,
        bytesTotal: 2048,
        currentPath: "/tmp/archive.tar");

    Assert.NotNull(observed);
    Assert.Equal(512, observed!.BytesDone);
    Assert.Equal(2048, observed.BytesTotal);
    Assert.Equal("/tmp/archive.tar", observed.CurrentPath);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~NativeSftpTransferInteropTests.NativeProgressCallback_TranslatesPayloadToManagedProgress" --no-restore -m:1
```

Expected: FAIL because the helper/callback marshaling does not exist yet.

**Step 3: Write minimal implementation**

In `NativeSshInterop.cs`:

- add a private delegate matching the native callback ABI
- add a private callback state object holding the managed `Action<NativeSftpTransferProgress>`
- add a small internal helper for test-only callback translation
- pass the callback function pointer and user data into the native `nova_ssh_sftp_transfer` call
- make callback exceptions non-fatal to the transfer

Do not redesign the public `INativeSshInterop` surface.

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~NativeSftpTransferInteropTests" --no-restore -m:1
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSftpTransferInteropTests.cs
git commit -m "feat: marshal native sftp progress callbacks"
```

### Task 3: Emit Progress From Rust File Copy Loops

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`
- Modify: `src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`

**Step 1: Write the failing end-to-end test**

Add a Docker-backed NativeSSH transfer test that records progress callbacks during a real file transfer:

```csharp
[Fact]
public async Task NativeSftp_CanReportProgressDuringDownload()
{
    // arrange remote file with enough content to produce multiple callbacks
    // capture progress payloads into a list
    // run NativeSshInterop.RunSftpTransfer(...)
    // assert at least one callback arrived before completion
    // assert one callback had BytesTotal > 0
}
```

Keep it file-based first. Do not start with directory aggregation.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~NativeSshDockerE2eTests.NativeSftp_CanReportProgressDuringDownload" --no-restore -m:1
```

Expected: FAIL because the native layer does not emit callbacks yet, or SKIP if Docker is unavailable locally. If skipped locally, still keep the test as the red test for CI/manual Docker runs.

**Step 3: Write minimal Rust implementation**

In `lib.rs`:

- extend the exported `nova_ssh_sftp_transfer(...)` signature to accept:
  - callback function pointer
  - callback user-data pointer
- define a small Rust progress emitter helper
- look up local file size for uploads and remote metadata size for downloads when available
- update `copy_file_with_cancellation(...)` to:
  - accumulate bytes copied
  - invoke the callback after each chunk
  - pass `current_path`
- for directory transfers, emit progress for each current file without attempting recursive total precomputation

In `NativeSshInterop.cs`:

- update the P/Invoke signature to match the new native ABI

**Step 4: Run verification**

Run:

```bash
cargo build --manifest-path src\NovaTerminal.App\native\rusty_ssh\Cargo.toml
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~NativeSftpTransferInteropTests|FullyQualifiedName~NativeSshDockerE2eTests.NativeSftp_CanReportProgressDuringDownload" --no-restore -m:1
```

Expected: Rust build PASS. Interop tests PASS. Docker progress test PASS when Docker is available.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs
git commit -m "feat: emit native sftp file progress"
```

### Task 4: Verify Service/UI Consumption Of Native Progress

**Files:**
- Modify: `src/NovaTerminal.App/Core/SftpService.cs`
- Test: `tests/NovaTerminal.Tests/Core/SftpServiceTests.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`

**Step 1: Write the failing tests**

Add one service-level test that proves repeated native progress callbacks mutate the job correctly:

```csharp
[Fact]
public void ApplyNativeTransferProgress_UpdatesDisplayStateForKnownTotals()
{
    var job = new TransferJob { State = TransferState.Running };

    SftpService.ApplyNativeTransferProgress(job, new NativeSftpTransferProgress
    {
        BytesDone = 1024,
        BytesTotal = 4096,
        CurrentPath = "/tmp/sample.bin"
    });

    Assert.Equal(0.25, job.Progress, 3);
    Assert.False(job.IsProgressIndeterminate);
    Assert.Contains("25%", job.StatusText, StringComparison.Ordinal);
}
```

Add one Docker directory-transfer test that confirms progress callbacks occur during a folder transfer, even if totals remain file-scoped.

**Step 2: Run tests to verify they fail or skip appropriately**

Run:

```bash
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c NativeSftpPlanApp --filter "FullyQualifiedName~SftpServiceTests" --no-restore -p:SkipCliShim=true -m:1
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~NativeSshDockerE2eTests.NativeSftp_CanReportProgressDuringDirectoryDownload" --no-restore -m:1
```

Expected: service test FAIL until formatting/state behavior matches; Docker directory test FAIL or SKIP until callbacks are observed.

**Step 3: Write minimal implementation**

- keep `SftpService.ApplyNativeTransferProgress(...)` as the single job-update mapping point
- only adjust it if needed for:
  - known-total percentage
  - indeterminate state when total is zero
  - current-path-aware display text if that becomes necessary

Do not add new UI surfaces in this task.

**Step 4: Run verification**

Run:

```bash
dotnet build src\NovaTerminal.App\NovaTerminal.App.csproj -c NativeSftpPlanApp --no-restore -p:SkipCliShim=true
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c NativeSftpPlanApp --filter "FullyQualifiedName~SftpServiceTests" --no-restore -p:SkipCliShim=true -m:1
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~NativeSftpTransferInteropTests|FullyQualifiedName~NativeSshDockerE2eTests" --no-restore -m:1
```

Expected: build PASS, service tests PASS, native Docker tests PASS when Docker is available.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/SftpService.cs tests/NovaTerminal.Tests/Core/SftpServiceTests.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs
git commit -m "test: verify native sftp progress propagation"
```

### Task 5: Update Docs And PR Notes

**Files:**
- Modify: `docs/USER_MANUAL.md`
- Modify: `docs/SSH_ROADMAP.md`

**Step 1: Write the minimal doc changes**

Document that NativeSSH transfers now show live native progress for file transfers and per-file progress for folder transfers when totals are known.

**Step 2: Verify docs reflect reality**

Manually confirm the text does not claim OpenSSH progress changes or recursive total aggregation that the code does not implement.

**Step 3: Commit**

```bash
git add docs/USER_MANUAL.md docs/SSH_ROADMAP.md
git commit -m "docs: describe native sftp live progress"
```
