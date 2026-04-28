# Native Remote Path Autocomplete Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add NativeSSH-only remote path autocomplete for active-session-backed remote-path inputs, starting with the transfer dialog.

**Architecture:** Add a dedicated native directory-listing request in the existing Rust/C# NativeSSH interop, gate autocomplete through an app-layer active-session registry, and surface suggestions through a reusable autocomplete control or controller instead of transfer-specific code. Keep completion traffic fully separate from the visible terminal stream.

**Tech Stack:** Avalonia UI, NativeSSH interop (`NativeSshInterop` + `rusty_ssh`), xUnit, Avalonia.Headless.XUnit, existing NativeSSH Docker tests

---

### Task 1: Add autocomplete path parsing and ranking primitives

**Files:**
- Create: `src/NovaTerminal.App/Services/Ssh/RemotePathAutocompleteQuery.cs`
- Create: `src/NovaTerminal.App/Models/RemotePathSuggestion.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/RemotePathAutocompleteQueryTests.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public void Parse_WhenGivenPartialAbsolutePath_SplitsParentAndPrefix()
{
    var query = RemotePathAutocompleteQuery.Parse("/mnt/media/mov");

    Assert.Equal("/mnt/media", query.ParentPath);
    Assert.Equal("mov", query.Prefix);
}

[Fact]
public void RankSuggestions_PrefersDirectoryPrefixMatchesBeforeFiles()
{
    var suggestions = RemotePathAutocompleteQuery.Rank(
        new[]
        {
            new RemotePathSuggestion("movie.mkv", "/mnt/media/movie.mkv", isDirectory: false),
            new RemotePathSuggestion("movies", "/mnt/media/movies", isDirectory: true)
        },
        prefix: "mov");

    Assert.Equal("movies", suggestions[0].DisplayName);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~RemotePathAutocompleteQueryTests" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: FAIL because the query parser and suggestion model do not exist yet.

**Step 3: Write minimal implementation**

Implement:

- `RemotePathSuggestion`
- `RemotePathAutocompleteQuery.Parse(string input)`
- `RemotePathAutocompleteQuery.Rank(...)`

Keep the rules minimal:

- support `~`, `/`, and partial leaf names
- directories rank before files within the same prefix-match tier
- alphabetical within the same tier

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Services/Ssh/RemotePathAutocompleteQuery.cs src/NovaTerminal.App/Models/RemotePathSuggestion.cs tests/NovaTerminal.Tests/Ssh/RemotePathAutocompleteQueryTests.cs
git commit -m "feat: add remote path autocomplete query primitives"
```

### Task 2: Add an active NativeSSH session registry

**Files:**
- Create: `src/NovaTerminal.App/Services/Ssh/ActiveSshSessionRegistry.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/ActiveSshSessionRegistryTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public void TryGetNativeSession_WhenRegisteredActiveNativeSessionExists_ReturnsDescriptor()
{
    var registry = new ActiveSshSessionRegistry();
    Guid sessionId = Guid.NewGuid();
    Guid profileId = Guid.NewGuid();

    registry.Register(new ActiveSshSessionDescriptor(
        sessionId,
        profileId,
        SshBackendKind.Native));

    Assert.True(registry.TryGet(sessionId, out ActiveSshSessionDescriptor? descriptor));
    Assert.Equal(profileId, descriptor!.ProfileId);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~ActiveSshSessionRegistryTests" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: FAIL because the registry does not exist yet.

**Step 3: Write minimal implementation**

Add:

- `ActiveSshSessionDescriptor`
- register/unregister/try-get APIs
- `TerminalPane` registration when an SSH session starts
- `TerminalPane` cleanup when the session exits or the pane disposes

Keep it as metadata only. Do not route terminal IO through the registry.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Services/Ssh/ActiveSshSessionRegistry.cs src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.Tests/Ssh/ActiveSshSessionRegistryTests.cs
git commit -m "feat: track active native ssh sessions for autocomplete"
```

### Task 3: Add native directory-listing interop in C#

**Files:**
- Modify: `src/NovaTerminal.Core/Ssh/Native/INativeSshInterop.cs`
- Modify: `src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs`
- Test: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshRemotePathInteropTests.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public void SerializeDirectoryListRequest_UsesGeneratedJsonMetadata()
{
    string json = NativeSshInterop.SerializeRemotePathListRequestForTests(
        connectionOptions,
        "/mnt/media");

    Assert.Contains("\"path\":\"/mnt/media\"", json);
}

[Fact]
public void DeserializeDirectoryListResponse_ReadsDirectoryFlags()
{
    const string json = """
    {"entries":[{"name":"movies","fullPath":"/mnt/media/movies","isDirectory":true}]}
    """;

    var entries = NativeSshInterop.DeserializeRemotePathListResponseForTests(json);

    Assert.Single(entries);
    Assert.True(entries[0].IsDirectory);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~NativeSshRemotePathInteropTests" --no-restore -m:1 --nologo -v:minimal
```

Expected: FAIL because the request/response types and API do not exist yet.

**Step 3: Write minimal implementation**

Add a new interop API, for example:

```csharp
IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(
    NativeSshConnectionOptions connectionOptions,
    string remotePath,
    CancellationToken cancellationToken);
```

Use source-generated JSON metadata just like the native transfer path. Keep the response shape small and explicit.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.Core/Ssh/Native/INativeSshInterop.cs src/NovaTerminal.Core/Ssh/Native/NativeSshInterop.cs tests/NovaTerminal.Core.Tests/Ssh/NativeSshRemotePathInteropTests.cs
git commit -m "feat: add native remote path listing interop"
```

### Task 4: Implement the Rust native directory-listing request

**Files:**
- Modify: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs`
- Test: `src/NovaTerminal.App/native/rusty_ssh/src/lib.rs` (unit tests)
- Optional: modify `src/NovaTerminal.App/native/rusty_ssh/Cargo.toml` only if a new crate is truly required

**Step 1: Write the failing Rust test**

Add a focused unit test near the current transfer tests that validates the list request against a known directory response shape.

Example shape:

```rust
#[test]
fn sftp_list_directory_returns_entries_with_directory_flags() {
    let response = run_list_directory_for_test("/tmp");
    assert!(response.entries.iter().any(|entry| entry.is_directory));
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
cargo test --manifest-path src/NovaTerminal.App/native/rusty_ssh/Cargo.toml sftp_list_directory
```

Expected: FAIL because the native listing entry point does not exist yet.

**Step 3: Write minimal implementation**

Add a new native export analogous to the transfer entry point:

- parse request JSON
- connect with the existing NativeSSH connection path
- open an SFTP client
- list one directory
- return entry name, full path, and directory flag

Do not add recursion, metadata expansion, or caching.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/native/rusty_ssh/src/lib.rs
git commit -m "feat: add native remote path listing request"
```

### Task 5: Add the app-layer remote path autocomplete service

**Files:**
- Create: `src/NovaTerminal.App/Services/Ssh/IRemotePathAutocompleteService.cs`
- Create: `src/NovaTerminal.App/Services/Ssh/RemotePathAutocompleteService.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/RemotePathAutocompleteServiceTests.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public async Task GetSuggestionsAsync_WhenNoActiveNativeSessionExists_ReturnsEmpty()
{
    var service = CreateServiceWithNoSession();

    var results = await service.GetSuggestionsAsync(profileId, sessionId, "/mnt/media/mov", CancellationToken.None);

    Assert.Empty(results);
}

[Fact]
public async Task GetSuggestionsAsync_FiltersNativeEntriesUsingResolvedParentPath()
{
    var service = CreateServiceWithEntries(
        "/mnt/media",
        new[]
        {
            new NativeRemotePathEntry("movies", "/mnt/media/movies", true),
            new NativeRemotePathEntry("music", "/mnt/media/music", true)
        });

    var results = await service.GetSuggestionsAsync(profileId, sessionId, "/mnt/media/mov", CancellationToken.None);

    Assert.Single(results);
    Assert.Equal("/mnt/media/movies", results[0].FullPath);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~RemotePathAutocompleteServiceTests" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: FAIL because the service does not exist yet.

**Step 3: Write minimal implementation**

Responsibilities:

- check the active session registry
- reject non-native or inactive sessions
- build native connection options from the active profile
- call the native listing API
- rank/filter suggestions with `RemotePathAutocompleteQuery`

Keep the service ignorant of popup/UI state.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Services/Ssh/IRemotePathAutocompleteService.cs src/NovaTerminal.App/Services/Ssh/RemotePathAutocompleteService.cs tests/NovaTerminal.Tests/Ssh/RemotePathAutocompleteServiceTests.cs
git commit -m "feat: add remote path autocomplete service"
```

### Task 6: Add a reusable remote-path autocomplete input surface

**Files:**
- Create: `src/NovaTerminal.App/Controls/RemotePathInput.axaml`
- Create: `src/NovaTerminal.App/Controls/RemotePathInput.axaml.cs`
- Create: `src/NovaTerminal.App/Models/RemotePathInputContext.cs`
- Test: `tests/NovaTerminal.Tests/Core/RemotePathInputTests.cs`

**Step 1: Write the failing UI tests**

```csharp
[AvaloniaFact]
public async Task RemotePathInput_WhenSuggestionsExist_ShowsPopupAndAcceptsTab()
{
    var control = new RemotePathInput();
    control.SetSuggestionsForTests(new[]
    {
        new RemotePathSuggestion("movies", "/mnt/media/movies", isDirectory: true)
    });

    control.Text = "/mnt/media/mov";
    await control.TriggerSuggestionsForTests();
    control.RaiseKeyDown(Key.Tab);

    Assert.Equal("/mnt/media/movies/", control.Text);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~RemotePathInputTests" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: FAIL because the reusable input control does not exist yet.

**Step 3: Write minimal implementation**

Add:

- textbox
- popup/listbox under the textbox
- debounce timer
- keyboard handling for `Up`, `Down`, `Tab`, `Enter`, `Escape`
- directory completion with trailing `/`

Keep the control generic. Do not embed transfer-specific logic in it.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/RemotePathInput.axaml src/NovaTerminal.App/Controls/RemotePathInput.axaml.cs src/NovaTerminal.App/Models/RemotePathInputContext.cs tests/NovaTerminal.Tests/Core/RemotePathInputTests.cs
git commit -m "feat: add reusable remote path autocomplete input"
```

### Task 7: Integrate autocomplete into the transfer dialog

**Files:**
- Modify: `src/NovaTerminal.App/Models/TransferDialogRequest.cs`
- Modify: `src/NovaTerminal.App/Controls/TransferDialog.axaml`
- Modify: `src/NovaTerminal.App/Controls/TransferDialog.axaml.cs`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/TransferDialogTests.cs`
- Test: `tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs`

**Step 1: Write the failing tests**

```csharp
[AvaloniaFact]
public void TransferDialog_BindsRemoteAutocompleteContext_FromRequest()
{
    var request = new TransferDialogRequest
    {
        Direction = TransferDirection.Download,
        Kind = TransferKind.File,
        RemotePath = "~/downloads",
        SessionId = sessionId,
        ProfileId = profileId
    };

    var dialog = new TransferDialog(request);

    var remoteInput = dialog.FindControl<RemotePathInput>("RemotePathInput");
    Assert.Equal(sessionId, remoteInput!.Context.SessionId);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~TransferDialogTests|FullyQualifiedName~MainWindowTransferFlowTests" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: FAIL because the transfer dialog request does not carry autocomplete context yet.

**Step 3: Write minimal implementation**

- extend `TransferDialogRequest` with the active session/profile context needed by autocomplete
- replace the raw `RemotePathBox` with `RemotePathInput`
- have `MainWindow` pass the active session/profile context when building the dialog request

Manual typing must remain valid when no suggestions are available.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Models/TransferDialogRequest.cs src/NovaTerminal.App/Controls/TransferDialog.axaml src/NovaTerminal.App/Controls/TransferDialog.axaml.cs src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.Tests/Core/TransferDialogTests.cs tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs
git commit -m "feat: enable remote autocomplete in transfer dialog"
```

### Task 8: Verify the end-to-end NativeSSH autocomplete path

**Files:**
- Modify: `tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs`
- Optional Docs: `docs/USER_MANUAL.md` if user-facing transfer instructions need one line of update

**Step 1: Write the failing E2E test**

Add one focused Docker test that lists a known remote directory and verifies at least one expected child entry is returned.

Example shape:

```csharp
[DockerFact]
public void NativeSftp_ListDirectory_ReturnsExpectedFixtureEntries()
{
    // Arrange docker fixture contents
    // Act through NativeSshInterop list-directory API
    // Assert expected file/directory names
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
$env:NOVATERM_ENABLE_DOCKER_E2E='1'
dotnet test tests/NovaTerminal.Core.Tests/NovaTerminal.Core.Tests.csproj -c NativeSftpPlan --filter "FullyQualifiedName~NativeSshDockerE2eTests.NativeSftp_ListDirectory" --no-restore -m:1 --nologo -v:minimal
```

Expected: FAIL because the E2E coverage does not exist yet.

**Step 3: Write minimal implementation**

Add the Docker-backed verification and, only if needed, a short user-manual note that remote autocomplete requires an active NativeSSH session.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Core.Tests/Ssh/NativeSshDockerE2eTests.cs docs/USER_MANUAL.md
git commit -m "test: cover native remote path autocomplete end to end"
```
