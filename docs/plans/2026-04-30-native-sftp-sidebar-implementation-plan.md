# Native SFTP Sidebar Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a pane-local, lightly navigable Native SFTP sidebar that uses live SSH context for transfer actions while keeping `SftpService` as the execution boundary.

**Architecture:** Add a small app-layer remote directory browser service plus a pane-local sidebar view model and Avalonia control hosted by `TerminalPane`. Reuse the existing native remote directory listing path and active session registry for data, keep alternate-screen hiding in the pane layer, and continue routing actual transfer jobs through `SftpService` and the existing local picker flow.

**Tech Stack:** C#, .NET 10, Avalonia, existing Native SSH interop, `SftpService`, xUnit, Avalonia.Headless.XUnit

---

### Task 1: Define sidebar models and directory browser contract

**Files:**
- Create: `src/NovaTerminal.App/Models/RemoteSidebarEntry.cs`
- Create: `src/NovaTerminal.App/Models/RemoteSidebarListingResult.cs`
- Create: `src/NovaTerminal.App/Services/Ssh/IRemoteDirectoryBrowserService.cs`
- Create: `src/NovaTerminal.App/Services/Ssh/RemoteSidebarStartPathResolver.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/RemoteSidebarStartPathResolverTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public void ResolveStartPath_PrefersPaneWorkingDirectory_OverProfileDefault()
{
    string resolved = RemoteSidebarStartPathResolver.Resolve(
        currentWorkingDirectory: "/srv/app",
        defaultRemoteDirectory: "~/downloads");

    Assert.Equal("/srv/app", resolved);
}
```

Add a second test that falls back from blank cwd to `profile.DefaultRemoteDir`, and then to `~`.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~RemoteSidebarStartPathResolverTests" -m:1 --nologo -v:minimal
```

Expected: FAIL because the resolver and sidebar models do not exist yet.

**Step 3: Write minimal implementation**

Add small explicit models:

```csharp
public sealed record RemoteSidebarEntry(
    string Name,
    string FullPath,
    bool IsDirectory);
```

```csharp
public static class RemoteSidebarStartPathResolver
{
    public static string Resolve(string? currentWorkingDirectory, string? defaultRemoteDirectory)
    {
        if (!string.IsNullOrWhiteSpace(currentWorkingDirectory))
        {
            return currentWorkingDirectory.Trim();
        }

        if (!string.IsNullOrWhiteSpace(defaultRemoteDirectory))
        {
            return defaultRemoteDirectory.Trim();
        }

        return "~";
    }
}
```

Define a browser contract such as:

```csharp
public interface IRemoteDirectoryBrowserService
{
    Task<RemoteSidebarListingResult> ListDirectoryAsync(
        Guid profileId,
        Guid sessionId,
        string remotePath,
        CancellationToken cancellationToken);
}
```

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Models/RemoteSidebarEntry.cs src/NovaTerminal.App/Models/RemoteSidebarListingResult.cs src/NovaTerminal.App/Services/Ssh/IRemoteDirectoryBrowserService.cs src/NovaTerminal.App/Services/Ssh/RemoteSidebarStartPathResolver.cs tests/NovaTerminal.Tests/Ssh/RemoteSidebarStartPathResolverTests.cs
git commit -m "feat: define remote sidebar contracts"
```

### Task 2: Implement the remote directory browser service

**Files:**
- Create: `src/NovaTerminal.App/Services/Ssh/RemoteDirectoryBrowserService.cs`
- Modify: `src/NovaTerminal.App/Services/Ssh/ActiveSshSessionRegistry.cs`
- Modify: `src/NovaTerminal.App/Services/Ssh/RemotePathAutocompleteService.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/RemoteDirectoryBrowserServiceTests.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/ActiveSshSessionRegistryTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task ListDirectoryAsync_WhenActiveNativeSessionExists_ReturnsDirectoriesBeforeFiles()
{
    Guid profileId = Guid.NewGuid();
    Guid sessionId = Guid.NewGuid();
    var registry = new ActiveSshSessionRegistry();
    registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));

    var interop = new RecordingNativeSshInterop(
        new[]
        {
            new NativeRemotePathEntry("b.txt", "/srv/b.txt", false),
            new NativeRemotePathEntry("alpha", "/srv/alpha", true)
        });

    var service = CreateService(registry, interop, CreateSshService(profileId));
    RemoteSidebarListingResult result = await service.ListDirectoryAsync(profileId, sessionId, "/srv", CancellationToken.None);

    Assert.True(result.IsSuccess);
    Assert.Collection(
        result.Entries,
        entry => Assert.Equal("alpha", entry.Name),
        entry => Assert.Equal("b.txt", entry.Name));
}
```

Add tests for:

- non-Native or missing session returns a failed/empty result without throwing
- permission/listing errors become inline error payloads
- blank remote path normalizes to `~`

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~RemoteDirectoryBrowserServiceTests" -m:1 --nologo -v:minimal
```

Expected: FAIL because the service does not exist yet.

**Step 3: Write minimal implementation**

Implement `RemoteDirectoryBrowserService` by reusing the same native connection preparation rules already used by `RemotePathAutocompleteService`:

- require an active Native SSH session from `ActiveSshSessionRegistry`
- load the profile from `SshConnectionService`
- build `NativeSshConnectionOptions`
- call `INativeSshInterop.ListRemoteDirectory(...)`
- sort directories first, then files, alphabetically
- return a result object with `Entries`, `ResolvedPath`, `IsSuccess`, and `ErrorMessage`

If shared native-connection setup emerges naturally, extract a small helper instead of duplicating large blocks.

**Step 4: Run test to verify it passes**

Run the same command as Step 2, then also run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~ActiveSshSessionRegistryTests" -m:1 --nologo -v:minimal
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Services/Ssh/RemoteDirectoryBrowserService.cs src/NovaTerminal.App/Services/Ssh/ActiveSshSessionRegistry.cs src/NovaTerminal.App/Services/Ssh/RemotePathAutocompleteService.cs tests/NovaTerminal.Tests/Ssh/RemoteDirectoryBrowserServiceTests.cs tests/NovaTerminal.Tests/Ssh/ActiveSshSessionRegistryTests.cs
git commit -m "feat: add native remote directory browser service"
```

### Task 3: Add a sidebar view model with navigation and state transitions

**Files:**
- Create: `src/NovaTerminal.App/ViewModels/Ssh/RemoteFilesSidebarViewModel.cs`
- Create: `src/NovaTerminal.App/ViewModels/Ssh/RemoteFilesSidebarEntryViewModel.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/RemoteFilesSidebarViewModelTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task OpenAsync_LoadsInitialPath_AndDisablesDownloadUntilSelectionExists()
{
    var service = new FakeRemoteDirectoryBrowserService("/srv", new[]
    {
        new RemoteSidebarEntry("logs", "/srv/logs", true)
    });

    var viewModel = new RemoteFilesSidebarViewModel(service);
    await viewModel.OpenAsync(
        profileId: Guid.NewGuid(),
        sessionId: Guid.NewGuid(),
        initialPath: "/srv",
        CancellationToken.None);

    Assert.True(viewModel.IsOpen);
    Assert.Equal("/srv", viewModel.CurrentPath);
    Assert.False(viewModel.CanDownloadSelected);
}
```

Add tests for:

- selecting an entry enables download
- navigating into a directory pushes back-stack state
- `NavigateUpAsync()` moves to parent
- `SetJumpToCurrentDirectoryCandidate("/srv/api")` exposes the jump affordance without forcing navigation
- listing failure sets inline error state and keeps the sidebar open

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~RemoteFilesSidebarViewModelTests" -m:1 --nologo -v:minimal
```

Expected: FAIL because the view model does not exist yet.

**Step 3: Write minimal implementation**

Create a small stateful view model with:

- `IsOpen`
- `IsLoading`
- `IsDisconnected`
- `CurrentPath`
- `JumpToCurrentDirectoryPath`
- `ObservableCollection<RemoteFilesSidebarEntryViewModel> Entries`
- `SelectedEntry`
- `CanDownloadSelected`
- `CanNavigateBack`
- `ErrorMessage`

Add async methods:

- `OpenAsync(...)`
- `Close()`
- `RefreshAsync()`
- `NavigateIntoSelectedDirectoryAsync()`
- `NavigateUpAsync()`
- `NavigateBackAsync()`
- `JumpToCurrentDirectoryAsync()`
- `MarkDisconnected()`

Keep navigation logic inside the view model so `TerminalPane` only routes events.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/ViewModels/Ssh/RemoteFilesSidebarViewModel.cs src/NovaTerminal.App/ViewModels/Ssh/RemoteFilesSidebarEntryViewModel.cs tests/NovaTerminal.Tests/Ssh/RemoteFilesSidebarViewModelTests.cs
git commit -m "feat: add remote files sidebar state model"
```

### Task 4: Add the Avalonia sidebar control

**Files:**
- Create: `src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml`
- Create: `src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/RemoteFilesSidebarTests.cs`

**Step 1: Write the failing test**

```csharp
[AvaloniaFact]
public void DownloadButton_IsDisabled_WhenNothingIsSelected()
{
    var viewModel = new RemoteFilesSidebarViewModel(new FakeRemoteDirectoryBrowserService());
    var control = new RemoteFilesSidebar
    {
        DataContext = viewModel
    };

    Button button = control.FindControl<Button>("BtnDownloadSelected")!;
    Assert.False(button.IsEnabled);
}
```

Add tests for:

- inline error text visibility when `ErrorMessage` is set
- jump-to-current-directory button visibility
- selected row activation triggers directory navigation for directories

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~RemoteFilesSidebarTests" -m:1 --nologo -v:minimal
```

Expected: FAIL because the control does not exist yet.

**Step 3: Write minimal implementation**

Create a narrow right-rail control with named elements:

```xml
<Button Name="BtnNavigateBack" />
<Button Name="BtnNavigateUp" />
<Button Name="BtnRefresh" />
<Button Name="BtnCloseSidebar" />
<Button Name="BtnJumpToCwd" />
<ListBox Name="RemoteEntriesList" />
<TextBlock Name="InlineErrorText" />
<Button Name="BtnUploadFile" />
<Button Name="BtnUploadFolder" />
<Button Name="BtnDownloadSelected" />
```

Bind enablement and visibility to the sidebar view model. Keep visuals simple and keyboard-friendly.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml.cs tests/NovaTerminal.Tests/Core/RemoteFilesSidebarTests.cs
git commit -m "feat: add remote files sidebar ui"
```

### Task 5: Host the sidebar in TerminalPane and wire pane lifecycle

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs`

**Step 1: Write the failing test**

```csharp
[AvaloniaFact]
public void Sidebar_HidesImmediately_WhenAltScreenBecomesActive()
{
    var pane = new TerminalPane();
    pane.ShowRemoteFilesSidebarForTest();

    pane.Buffer!.IsAltScreenActive = true;
    pane.HandleAltScreenChangedForTest(true);

    Assert.False(pane.IsRemoteFilesSidebarVisibleForTest());
}
```

Add tests for:

- sidebar entry point is disabled or hidden for non-Native SSH panes
- opening the sidebar uses cwd first, then profile default
- `WorkingDirectoryChanged` updates jump-to-current-directory affordance instead of forcing navigation

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~TerminalPaneRemoteFilesSidebarTests" -m:1 --nologo -v:minimal
```

Expected: FAIL because the sidebar host and test hooks do not exist yet.

**Step 3: Write minimal implementation**

In `TerminalPane.axaml`, add a sidebar host beside the terminal layer rather than inside terminal content.

In `TerminalPane.axaml.cs`:

- create and bind a `RemoteFilesSidebarViewModel`
- add a pane-local toggle action
- hide the sidebar when alt-screen is active
- close or disable it on disconnect
- update jump-to-current-directory state from pane cwd changes

In `MainWindow.axaml.cs`, keep transfer/job initiation in the window layer, but let pane-originated sidebar actions flow upward through explicit events.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml src/NovaTerminal.App/Controls/TerminalPane.axaml.cs src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs
git commit -m "feat: host remote files sidebar in terminal pane"
```

### Task 6: Route sidebar upload and download actions through the existing transfer system

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Modify: `src/NovaTerminal.App/Models/TransferDialogRequest.cs`
- Modify: `src/NovaTerminal.App/Controls/TransferDialog.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs`
- Test: `tests/NovaTerminal.Tests/Core/TransferDialogRequestTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task SidebarDownload_UsesSelectedRemoteEntry_AsTransferRemotePath()
{
    var window = new TestMainWindow
    {
        NextTransferDialogResult = TransferDialogResult.CreateConfirmed(
            localPath: @"D:\downloads\logs",
            remotePath: "/srv/logs")
    };

    await window.StartSidebarDownloadForTest(
        profile: CreateNativeProfile(),
        sessionId: Guid.NewGuid(),
        selectedRemotePath: "/srv/logs",
        kind: TransferKind.Folder);

    Assert.NotNull(window.QueuedJob);
    Assert.Equal("/srv/logs", window.QueuedJob!.RemotePath);
}
```

Add tests for:

- sidebar upload targets the currently displayed directory
- download remains disabled without selection
- manual transfer dialog fallback still works for explicit typed-path flows

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~MainWindowTransferFlowTests|FullyQualifiedName~TransferDialogRequestTests" -m:1 --nologo -v:minimal
```

Expected: FAIL because sidebar-originated transfer requests do not exist yet.

**Step 3: Write minimal implementation**

Add explicit pane-to-window events for:

- upload file to current remote directory
- upload folder to current remote directory
- download selected remote entry

Update `TransferDialogRequest` only if needed to distinguish:

- manual transfer flow with editable remote path
- sidebar flow with preselected remote path or remote directory target

Keep `SftpService` job creation in `MainWindow.axaml.cs` so transfer ownership does not move into the pane.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml.cs src/NovaTerminal.App/MainWindow.axaml.cs src/NovaTerminal.App/Models/TransferDialogRequest.cs src/NovaTerminal.App/Controls/TransferDialog.axaml.cs tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs tests/NovaTerminal.Tests/Core/TransferDialogRequestTests.cs
git commit -m "feat: route sidebar transfers through sftp service"
```

### Task 7: Document and harden the new sidebar flow

**Files:**
- Modify: `docs/USER_MANUAL.md`
- Modify: `docs/plans/2026-04-30-native-sftp-sidebar-design.md`
- Test: `tests/NovaTerminal.Tests/Ssh/RemoteDirectoryBrowserServiceTests.cs`
- Test: `tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs`

**Step 1: Write the failing test**

Add one narrow regression test such as:

```csharp
[Fact]
public async Task MarkDisconnected_KeepsSidebarOpenButDisablesTransferActions()
{
    var viewModel = new RemoteFilesSidebarViewModel(new FakeRemoteDirectoryBrowserService("/srv", []));
    await viewModel.OpenAsync(Guid.NewGuid(), Guid.NewGuid(), "/srv", CancellationToken.None);

    viewModel.MarkDisconnected();

    Assert.True(viewModel.IsOpen);
    Assert.True(viewModel.IsDisconnected);
    Assert.False(viewModel.CanDownloadSelected);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~RemoteFilesSidebarViewModelTests.MarkDisconnected|FullyQualifiedName~TerminalPaneRemoteFilesSidebarTests" -m:1 --nologo -v:minimal
```

Expected: FAIL until disconnect hardening is complete.

**Step 3: Write minimal implementation**

- tighten disconnect and retry behavior
- ensure alt-screen hide/show behavior remains deterministic
- update `docs/USER_MANUAL.md` to describe the new Native SSH sidebar flow and the remaining manual dialog fallback

**Step 4: Run test to verify it passes**

Run the same command as Step 2, then the broader sidebar-related test filters.

Expected: PASS.

**Step 5: Commit**

```bash
git add docs/USER_MANUAL.md tests/NovaTerminal.Tests/Ssh/RemoteDirectoryBrowserServiceTests.cs tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs src/NovaTerminal.App/ViewModels/Ssh/RemoteFilesSidebarViewModel.cs
git commit -m "docs: describe native sftp sidebar flow"
```
