# Native SFTP Transfers Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace NativeSSH profile transfers' external `scp` dependency with native SFTP upload/download support built on `russh-sftp`.

**Architecture:** Keep the transfer subsystem separate from terminal VT parsing/rendering. Add a NativeSSH transfer path beside the existing external OpenSSH `scp` path, and route only `SshBackendKind.Native` jobs through the new native SFTP bridge. The Rust backend owns SSH/SFTP protocol work; C# `SftpService` owns queueing, progress, job state, and UI notifications.

**Tech Stack:** C#/.NET 10, Avalonia, existing `SftpService`, Rust native backend in `src/NovaTerminal.App/native/rusty_ssh`, `russh`, `russh-sftp`, FFI exported from Rust to C#.

---

## Non-Goals

- Do not modify VT parsing/rendering.
- Do not render transfer UI inside terminal grid content.
- Do not replace the OpenSSH profile transfer path in this project phase.
- Do not build a full file browser.
- Do not implement native SCP protocol unless `russh-sftp` proves unusable.

## Target Behavior

- OpenSSH profiles continue to use the current external `scp` implementation.
- NativeSSH profiles use native SFTP for upload/download.
- File transfers report progress as bytes done/total where the remote server exposes size.
- Folder transfers recurse deterministically.
- Failed native transfers produce actionable `TransferJob.LastError` text.
- Existing Transfer Center/status UI continues to work through `SftpService.JobUpdated`.

## Task 1: Add Rust SFTP Dependency And Compile Boundary

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/Cargo.toml`
- Modify: `src/NovaTerminal.App/native/rusty_ssh/Cargo.lock`

**Step 1: Add the dependency**

Add `russh-sftp` using a version compatible with the existing `russh` dependency in `Cargo.toml`.

Example shape:

```toml
russh-sftp = "2"
```

If the selected version pulls an incompatible `russh`, prefer a compatible `russh-sftp` version before changing Nova's existing `russh` version.

**Step 2: Build to expose version conflicts**

Run:

```powershell
cargo build --manifest-path src\NovaTerminal.App\native\rusty_ssh\Cargo.toml
```

Expected: either build succeeds, or dependency errors clearly identify version mismatch.

**Step 3: Resolve dependency mismatch only if needed**

If `russh-sftp` requires a different `russh`, test the smallest compatible version adjustment. Do not refactor session code in this task.

**Step 4: Commit**

```powershell
git add src/NovaTerminal.App/native/rusty_ssh/Cargo.toml src/NovaTerminal.App/native/rusty_ssh/Cargo.lock
git commit -m "build: add native sftp dependency"
```

## Task 2: Define Native Transfer FFI Contract

**Files:**
- Modify: `src/NovaTerminal.Core/Ssh/Native/INativeSshInterop.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs`
- Create: `src/NovaTerminal.Core/Ssh/Native/NativeSftpTransferModels.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSftpTransferInteropTests.cs`

**Step 1: Write failing model/interop tests**

Create tests for DTO defaults and interop argument validation:

```csharp
using NovaTerminal.Core.Ssh.Native;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeSftpTransferInteropTests
{
    [Fact]
    public void TransferOptions_RequireLocalAndRemotePaths()
    {
        var options = new NativeSftpTransferOptions();

        Assert.Throws<ArgumentException>(() => options.Validate());
    }
}
```

**Step 2: Run test to verify it fails**

Run:

```powershell
$env:SKIP_RUST_NATIVE_BUILD='1'
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~NativeSftpTransferInteropTests" --no-restore -m:1
```

Expected: compile failure because the model does not exist.

**Step 3: Add C# models**

Create `NativeSftpTransferModels.cs`:

```csharp
namespace NovaTerminal.Core.Ssh.Native;

public enum NativeSftpTransferDirection
{
    Upload,
    Download
}

public enum NativeSftpTransferKind
{
    File,
    Directory
}

public sealed class NativeSftpTransferOptions
{
    public NativeSftpTransferDirection Direction { get; set; }
    public NativeSftpTransferKind Kind { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LocalPath))
        {
            throw new ArgumentException("Local path is required.", nameof(LocalPath));
        }

        if (string.IsNullOrWhiteSpace(RemotePath))
        {
            throw new ArgumentException("Remote path is required.", nameof(RemotePath));
        }
    }
}

public sealed class NativeSftpTransferProgress
{
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public string CurrentPath { get; init; } = string.Empty;
}
```

**Step 4: Add interop surface**

Extend `INativeSshInterop` with a transfer method that takes an existing NativeSSH connection profile/options rather than an existing terminal session handle:

```csharp
void RunSftpTransfer(
    NativeSshConnectionOptions connectionOptions,
    NativeSftpTransferOptions transferOptions,
    Action<NativeSftpTransferProgress>? progress,
    CancellationToken cancellationToken);
```

Rationale: first implementation can open a separate native SSH connection for transfer. Do not try to share the live terminal session handle yet.

**Step 5: Run tests**

Expected: model tests pass; interop compile may fail until `NativeSshInterop` is updated.

**Step 6: Add throwing stub in `NativeSshInterop`**

Implement the interface with:

```csharp
public void RunSftpTransfer(
    NativeSshConnectionOptions connectionOptions,
    NativeSftpTransferOptions transferOptions,
    Action<NativeSftpTransferProgress>? progress,
    CancellationToken cancellationToken)
{
    throw new NotImplementedException("Native SFTP transfer is not implemented yet.");
}
```

**Step 7: Run tests**

Expected: tests compile/pass.

**Step 8: Commit**

```powershell
git add src/NovaTerminal.Core/Ssh/Native tests/NovaTerminal.Core.Tests/Ssh/NativeSftpTransferInteropTests.cs
git commit -m "feat: define native sftp transfer interop contract"
```

## Task 3: Route NativeSSH Transfer Jobs Separately

**Files:**
- Modify: `src/NovaTerminal.App/Core/SftpService.cs`
- Test: `tests/NovaTerminal.Tests/Core/SftpServiceTests.cs`

**Step 1: Write failing routing tests**

Add tests proving Native profiles do not build external `scp` start info:

```csharp
[Fact]
public void SelectTransferBackend_ForNativeProfile_UsesNativeSftp()
{
    var profile = new TerminalProfile
    {
        Type = ConnectionType.SSH,
        SshBackendKind = SshBackendKind.Native
    };

    Assert.Equal(SftpTransferBackend.NativeSftp, SftpService.SelectTransferBackend(profile));
}

[Fact]
public void SelectTransferBackend_ForOpenSshProfile_UsesExternalScp()
{
    var profile = new TerminalProfile
    {
        Type = ConnectionType.SSH,
        SshBackendKind = SshBackendKind.OpenSsh
    };

    Assert.Equal(SftpTransferBackend.ExternalScp, SftpService.SelectTransferBackend(profile));
}
```

**Step 2: Run test to verify it fails**

Run:

```powershell
$env:SKIP_RUST_NATIVE_BUILD='1'
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~SftpServiceTests" --no-restore -p:SkipCliShim=true -m:1
```

Expected: compile failure because `SftpTransferBackend` and `SelectTransferBackend` do not exist.

**Step 3: Add backend selector**

In `SftpService.cs`, add:

```csharp
internal enum SftpTransferBackend
{
    ExternalScp,
    NativeSftp
}

internal static SftpTransferBackend SelectTransferBackend(TerminalProfile profile)
{
    ArgumentNullException.ThrowIfNull(profile);
    return profile.SshBackendKind == SshBackendKind.Native
        ? SftpTransferBackend.NativeSftp
        : SftpTransferBackend.ExternalScp;
}
```

Use the fully-qualified or imported `SshBackendKind` already used in this file.

**Step 4: Update `RunJobAsync` branch**

Refactor:

```csharp
if (SelectTransferBackend(profile) == SftpTransferBackend.NativeSftp)
{
    await RunNativeSftpJobAsync(job, profile, cancellationToken: default);
}
else
{
    await RunExternalScpJobAsync(job, profile, settings, storeProfiles);
}
```

Keep the current external `scp` code in `RunExternalScpJobAsync` with minimal movement.

**Step 5: Add temporary native stub behavior**

`RunNativeSftpJobAsync` should throw:

```csharp
throw new NotImplementedException("Native SFTP transfers are not implemented yet.");
```

This keeps routing explicit before implementing Rust.

**Step 6: Run tests**

Expected: routing tests pass.

**Step 7: Commit**

```powershell
git add src/NovaTerminal.App/Core/SftpService.cs tests/NovaTerminal.Tests/Core/SftpServiceTests.cs
git commit -m "feat: route native ssh transfers to native sftp backend"
```

## Task 4: Build Native Connection Options For Transfer

**Files:**
- Modify: `src/NovaTerminal.App/Core/SftpService.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Native/NativeJumpHostConnector.cs` if needed
- Test: `tests/NovaTerminal.Tests/Core/SftpServiceTests.cs`

**Step 1: Write failing options test**

Add a test that turns a `TerminalProfile` into native connection options with host/user/port and profile context.

Example:

```csharp
[Fact]
public void BuildNativeTransferConnectionOptions_UsesProfileTarget()
{
    var profile = new TerminalProfile
    {
        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        Type = ConnectionType.SSH,
        SshBackendKind = SshBackendKind.Native,
        SshHost = "prod.internal",
        SshUser = "ops",
        SshPort = 2200
    };

    NativeSshConnectionOptions options = SftpService.BuildNativeTransferConnectionOptions(profile);

    Assert.Equal("prod.internal", options.Host);
    Assert.Equal("ops", options.User);
    Assert.Equal(2200, options.Port);
}
```

**Step 2: Run test to verify it fails**

Expected: compile failure because helper is absent.

**Step 3: Implement helper using existing NativeSSH connection planning**

Prefer existing code in `NativeJumpHostConnector` rather than duplicating jump-host logic. If its current method is inaccessible, add a small public/internal method rather than copying logic into `SftpService`.

**Step 4: Run tests**

Expected: pass.

**Step 5: Commit**

```powershell
git add src/NovaTerminal.App/Core/SftpService.cs src/NovaTerminal.Core/Ssh/Native/NativeJumpHostConnector.cs tests/NovaTerminal.Tests/Core/SftpServiceTests.cs
git commit -m "feat: build native transfer connection options"
```

## Task 5: Implement Rust FFI Transfer Skeleton

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`
- Modify: `src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSftpTransferInteropTests.cs`

**Step 1: Write failing interop call test with fake invalid args**

Test that `NativeSshInterop.RunSftpTransfer` validates null/empty paths before calling native code.

```csharp
[Fact]
public void RunSftpTransfer_WithEmptyPaths_ThrowsArgumentException()
{
    var interop = new NativeSshInterop();
    var connection = new NativeSshConnectionOptions { Host = "example", User = "u", Port = 22 };
    var transfer = new NativeSftpTransferOptions();

    Assert.Throws<ArgumentException>(() =>
        interop.RunSftpTransfer(connection, transfer, null, CancellationToken.None));
}
```

**Step 2: Run test to verify it fails**

Expected: fails until validation is wired.

**Step 3: Add Rust exported function stub**

In `lib.rs`, export a function such as:

```rust
#[no_mangle]
pub extern "C" fn nova_ssh_sftp_transfer(
    request_json: *const c_char,
    response_json: *mut *mut c_char,
) -> c_int {
    // Parse later. For now return NOT_IMPLEMENTED.
}
```

Use the repo's existing result-code and string-freeing patterns. Do not invent a second ownership model.

**Step 4: Add C# P/Invoke declaration**

In `NativeSshInterop.NativeMethods`, add the matching DllImport.

**Step 5: Implement validation and call path**

`RunSftpTransfer` should:

- validate connection options
- validate transfer options
- serialize a request DTO
- call native function
- throw clear exception on non-zero result

**Step 6: Run tests**

Expected: validation tests pass; not-implemented test can assert clear `NotImplementedException` or `InvalidOperationException` message.

**Step 7: Commit**

```powershell
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSftpTransferInteropTests.cs
git commit -m "feat: add native sftp ffi skeleton"
```

## Task 6: Implement Native File Download

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`
- Modify: `src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`

**Step 1: Add Docker E2E failing test**

Add a trait-marked test using the existing Docker SSH fixture. It should create a remote file, download it with native SFTP, and compare contents.

Pseudo-shape:

```csharp
[Fact]
[Trait("Target", "NativeSsh")]
public async Task NativeSftp_CanDownloadFile()
{
    using DockerSshFixture fixture = await DockerSshFixture.StartAsync();
    string localPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");

    var interop = new NativeSshInterop();
    var connection = fixture.CreateNativeConnectionOptions();
    var transfer = new NativeSftpTransferOptions
    {
        Direction = NativeSftpTransferDirection.Download,
        Kind = NativeSftpTransferKind.File,
        RemotePath = "/tmp/native-sftp-source.txt",
        LocalPath = localPath
    };

    interop.RunSftpTransfer(connection, transfer, null, CancellationToken.None);

    Assert.Equal("expected", File.ReadAllText(localPath));
}
```

Adapt to actual fixture APIs. If fixture lacks a command helper, add setup by existing SSH session command.

**Step 2: Run test to verify it fails**

Run only this test; expect native not-implemented failure.

**Step 3: Implement Rust download**

Rust implementation outline:

- create runtime or reuse current runtime setup pattern
- connect using existing native SSH auth path
- open SFTP subsystem with `russh-sftp`
- stat remote file to get size if possible
- create local file parent directory if needed
- read remote file in chunks
- write to local file
- emit progress after each chunk
- close handles

**Step 4: Wire progress JSON**

If the initial FFI cannot callback progress safely, write progress as final response only for this task. Add real callbacks in Task 8.

**Step 5: Run Docker E2E test**

Expected: pass.

**Step 6: Commit**

```powershell
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs
git commit -m "feat: support native sftp file download"
```

## Task 7: Implement Native File Upload

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`

**Step 1: Add failing upload E2E test**

Create local temp file, upload to `/tmp`, then verify by reading through remote command or downloading back.

**Step 2: Run test to verify it fails**

Expected: upload not implemented.

**Step 3: Implement Rust upload**

Implementation outline:

- open local file
- ensure remote parent path exists only if explicitly requested; do not silently create arbitrary parent dirs for file upload in this task
- create/truncate remote file
- stream chunks local to remote
- close handles

**Step 4: Run upload/download E2E tests**

Expected: pass.

**Step 5: Commit**

```powershell
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs
git commit -m "feat: support native sftp file upload"
```

## Task 8: Wire Native SFTP Into `SftpService`

**Files:**
- Modify: `src/NovaTerminal.App/Core/SftpService.cs`
- Test: `tests/NovaTerminal.Tests/Core/SftpServiceTests.cs`

**Step 1: Add failing service test with fake native transfer runner**

Introduce a small interface only if needed:

```csharp
internal interface INativeSftpTransferRunner
{
    void Run(
        NativeSshConnectionOptions connectionOptions,
        NativeSftpTransferOptions transferOptions,
        Action<NativeSftpTransferProgress>? progress,
        CancellationToken cancellationToken);
}
```

Test that a Native profile job calls this runner and marks completion.

**Step 2: Run test to verify it fails**

Expected: compile failure or service still uses stub.

**Step 3: Inject runner into service**

Because `SftpService.Instance` is static today, keep production constructor behavior intact and add an internal constructor for tests:

```csharp
internal SftpService(INativeSftpTransferRunner? nativeRunner)
{
    _nativeRunner = nativeRunner ?? new NativeSftpTransferRunner(new NativeSshInterop());
}
```

Do not change public API unless needed.

**Step 4: Implement `RunNativeSftpJobAsync`**

Map:

- `TransferDirection.Upload` -> `NativeSftpTransferDirection.Upload`
- `TransferDirection.Download` -> `NativeSftpTransferDirection.Download`
- `TransferKind.File` -> `NativeSftpTransferKind.File`
- `TransferKind.Folder` -> `NativeSftpTransferKind.Directory`

Progress callback updates:

```csharp
job.BytesDone = progress.BytesDone;
job.BytesTotal = progress.BytesTotal;
job.Progress = progress.BytesTotal > 0
    ? Math.Clamp((double)progress.BytesDone / progress.BytesTotal, 0, 1)
    : 0;
```

Always marshal `JobUpdated` through `Dispatcher.UIThread.Post`, matching existing service behavior.

**Step 5: Run service tests**

Expected: pass.

**Step 6: Commit**

```powershell
git add src/NovaTerminal.App/Core/SftpService.cs tests/NovaTerminal.Tests/Core/SftpServiceTests.cs
git commit -m "feat: wire native sftp transfers into service"
```

## Task 9: Add Directory Transfer Recursion

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`

**Step 1: Add failing directory download test**

Remote directory:

```text
/tmp/native-sftp-dir/a.txt
/tmp/native-sftp-dir/nested/b.txt
```

Download to local temp directory and assert both files exist with exact contents.

**Step 2: Add failing directory upload test**

Local directory with nested file uploads to `/tmp/native-sftp-uploaded-dir`; verify remote files.

**Step 3: Implement deterministic recursion**

Rules:

- directory download to selected local folder should create a child directory named after remote directory basename
- directory upload to remote destination should create a child directory named after local directory basename unless remote path already clearly names target directory
- skip symlink traversal in first version; copy symlink target only if SFTP server reports it as regular file
- sort directory entries by name before transferring for deterministic tests

**Step 4: Run directory E2E tests**

Expected: pass.

**Step 5: Commit**

```powershell
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs
git commit -m "feat: support native sftp directory transfers"
```

## Task 10: Add Cancellation And Better Errors

**Files:**
- Modify: `src/NovaTerminal.App/Core/SftpService.cs`
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`
- Test: `tests/NovaTerminal.Tests/Core/SftpServiceTests.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`

**Step 1: Add cancellation tests at service level**

Test cancellation marks `TransferState.Canceled` and does not mark completed.

**Step 2: Add error mapping tests**

Cover:

- remote file missing
- local file missing on upload
- permission denied
- auth failed

**Step 3: Implement cancellation token checks**

Check cancellation:

- before opening connection
- between file chunks
- between directory entries

**Step 4: Implement stable error messages**

Map Rust errors to short messages:

- `Remote path not found: <path>`
- `Local path not found: <path>`
- `Permission denied: <path>`
- `Authentication failed`
- `Native SFTP failed: <details>`

**Step 5: Run focused tests**

Expected: pass.

**Step 6: Commit**

```powershell
git add src/NovaTerminal.App/Core/SftpService.cs src/NovaTerminal.App/native/rusty_ssh/src/lib.rs tests
git commit -m "feat: add native sftp cancellation and errors"
```

## Task 11: Remove NativeSSH AskPass Bridge From Transfer Path

**Files:**
- Modify: `src/NovaTerminal.App/Core/SftpService.cs`
- Modify: `tests/NovaTerminal.Tests/Core/SftpServiceTests.cs`
- Optional Modify: `src/NovaTerminal.App/Core/SshAskPassCommand.cs`

**Step 1: Update tests**

Replace current tests that assert NativeSSH configures `SSH_ASKPASS` with tests asserting NativeSSH selects `NativeSftp` and does not build `scp` start info.

**Step 2: Run tests to verify they fail**

Expected: fail while old askpass branch remains.

**Step 3: Remove NativeSSH askpass transfer branch**

Keep `SshAskPassCommand` only if still needed for OpenSSH profile UX. If it is only used by NativeSSH transfers, delete it and remove CLI/App mode wiring.

**Step 4: Run tests**

Expected: pass.

**Step 5: Commit**

```powershell
git add src/NovaTerminal.App/Core/SftpService.cs tests/NovaTerminal.Tests/Core/SftpServiceTests.cs src/NovaTerminal.App/Core/SshAskPassCommand.cs src/NovaTerminal.App/Program.cs src/NovaTerminal.Cli/Program.cs
git commit -m "refactor: remove native ssh transfer askpass bridge"
```

## Task 12: Documentation And Manual Verification

**Files:**
- Modify: `docs/USER_MANUAL.md`
- Modify: `docs/SSH_ROADMAP.md` if present/relevant
- Modify: `docs/ROADMAP.md` if status needs updating

**Step 1: Update user docs**

Document:

- OpenSSH transfers use system `scp`
- NativeSSH transfers use built-in native SFTP
- Native SFTP supports file/folder upload/download
- known limitations, if any

**Step 2: Manual verification checklist**

Run the app and verify:

- NativeSSH password-auth profile: download file
- NativeSSH password-auth profile: upload file
- NativeSSH identity-file profile: download file
- NativeSSH folder download
- NativeSSH folder upload
- OpenSSH profile still transfers through existing path
- Transfer Center status updates
- cancel during large transfer
- missing remote path shows useful error

**Step 3: Full focused test command**

Run:

```powershell
$env:SKIP_RUST_NATIVE_BUILD='1'
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c NativeSftpFinal --filter "FullyQualifiedName~SftpServiceTests" --no-restore -p:SkipCliShim=true -m:1
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c NativeSftpFinal --filter "FullyQualifiedName~NativeSftp|FullyQualifiedName~NativeSshDockerE2eTests" --no-restore -m:1
```

Expected: all focused tests pass.

**Step 4: Commit**

```powershell
git add docs tests src
git commit -m "docs: document native sftp transfers"
```

## Rollout Notes

- Keep the external `scp` path as fallback for OpenSSH profiles.
- If native SFTP fails for a NativeSSH profile, do not silently fall back to external `scp`; report the native error. Silent fallback would reintroduce duplicate auth and platform-specific behavior.
- If `russh-sftp` integration blocks on version incompatibility for more than one day, stop and reassess before writing protocol code from scratch.

## Final Verification

Before claiming completion:

```powershell
$env:SKIP_RUST_NATIVE_BUILD='1'
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c NativeSftpFinal --no-restore -p:SkipCliShim=true -m:1
dotnet test tests\NovaTerminal.Core.Tests\NovaTerminal.Core.Tests.csproj -c NativeSftpFinal --no-restore -m:1
```

Expected: all tests pass, or any unrelated failures are documented with exact test names and failure messages.
