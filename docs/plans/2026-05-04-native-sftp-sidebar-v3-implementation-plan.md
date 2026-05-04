# Native SFTP Sidebar V3 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Refine the Native SSH remote-files sidebar into a denser two-column utility rail with host identity and modified-time metadata while preserving NovaTerminal's terminal-first SFTP boundaries.

**Architecture:** Keep `TerminalPane` as the pane-local host, keep the remote directory browser service as a one-directory-at-a-time listing boundary, expand the remote entry contract to carry modified metadata, and keep `MainWindow` plus `SftpService` responsible for picker invocation and transfer execution. This is a visual and metadata polish pass, not a remote file-manager expansion.

**Tech Stack:** C#, .NET 10, Avalonia, xUnit, Avalonia.Headless.XUnit, existing Native SSH remote directory browser service and sidebar view model.

---

### Task 1: Extend remote sidebar entry metadata

**Files:**
- Modify: `src/NovaTerminal.App/Models/RemoteSidebarEntry.cs`
- Modify: `src/NovaTerminal.App/Models/RemoteSidebarListingResult.cs`
- Modify: `src/NovaTerminal.App/Services/Ssh/RemoteDirectoryBrowserService.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/RemoteDirectoryBrowserServiceTests.cs`

**Step 1: Write the failing test**

Add a test that verifies a remote listing entry can carry modified-time metadata through the directory browser service result.

Example shape:

```csharp
[Fact]
public async Task ListDirectoryAsync_PreservesModifiedTimeMetadata()
{
    RemoteSidebarListingResult result = await service.ListDirectoryAsync(profileId, sessionId, "/srv", CancellationToken.None);

    RemoteSidebarEntry entry = Assert.Single(result.Entries);
    Assert.Equal(new DateTime(2026, 5, 4, 20, 15, 0, DateTimeKind.Utc), entry.ModifiedAtUtc);
}
```

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj --filter "FullyQualifiedName~RemoteDirectoryBrowserServiceTests" /p:UseAppHost=false /p:OutDir=D:\projects\nova2\.artifacts\sidebar-v3-build\
```

Expected: FAIL because `RemoteSidebarEntry` does not expose modified metadata yet.

**Step 3: Write minimal implementation**

- add nullable modified metadata to `RemoteSidebarEntry`
- keep constructors explicit and small
- map modified timestamps from the remote listing response in `RemoteDirectoryBrowserService`
- preserve existing success/failure behavior in `RemoteSidebarListingResult`

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Models/RemoteSidebarEntry.cs src/NovaTerminal.App/Models/RemoteSidebarListingResult.cs src/NovaTerminal.App/Services/Ssh/RemoteDirectoryBrowserService.cs tests/NovaTerminal.Tests/Ssh/RemoteDirectoryBrowserServiceTests.cs
git commit -m "feat: add remote sidebar modified metadata"
```

### Task 2: Surface modified metadata in the sidebar view model

**Files:**
- Modify: `src/NovaTerminal.App/ViewModels/Ssh/RemoteFilesSidebarEntryViewModel.cs`
- Modify: `src/NovaTerminal.App/ViewModels/Ssh/RemoteFilesSidebarViewModel.cs`
- Test: `tests/NovaTerminal.Tests/Ssh/RemoteFilesSidebarViewModelTests.cs`

**Step 1: Write the failing test**

Add tests that verify:

- modified metadata is surfaced on each sidebar entry view model
- missing metadata falls back to a placeholder-safe display value

Example shape:

```csharp
[Fact]
public async Task OpenAsync_MapsModifiedMetadataToEntryViewModels()
{
    await viewModel.OpenAsync(profileId, sessionId, "/srv", CancellationToken.None);

    RemoteFilesSidebarEntryViewModel entry = Assert.Single(viewModel.Entries);
    Assert.Equal("May 04", entry.ModifiedDisplayText);
}
```

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj --filter "FullyQualifiedName~RemoteFilesSidebarViewModelTests" /p:UseAppHost=false /p:OutDir=D:\projects\nova2\.artifacts\sidebar-v3-build\
```

Expected: FAIL because the entry view model does not expose modified display data yet.

**Step 3: Write minimal implementation**

- add a display-ready modified property to `RemoteFilesSidebarEntryViewModel`
- keep formatting compact and deterministic for tests
- do not change navigation, selection, or download enablement behavior

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/ViewModels/Ssh/RemoteFilesSidebarEntryViewModel.cs src/NovaTerminal.App/ViewModels/Ssh/RemoteFilesSidebarViewModel.cs tests/NovaTerminal.Tests/Ssh/RemoteFilesSidebarViewModelTests.cs
git commit -m "feat: expose modified metadata in sidebar entries"
```

### Task 3: Redesign the sidebar header and path chrome

**Files:**
- Modify: `src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml`
- Modify: `src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/RemoteFilesSidebarTests.cs`

**Step 1: Write the failing test**

Add a headless Avalonia test that asserts the new compact header structure exists.

Example shape:

```csharp
[AvaloniaFact]
public void Sidebar_UsesHostHeader_AndCompactPathRow()
{
    var control = new RemoteFilesSidebar();

    Assert.NotNull(control.FindControl<TextBlock>("HostTitleText"));
    Assert.NotNull(control.FindControl<TextBlock>("HostSubtitleText"));
    Assert.NotNull(control.FindControl<TextBlock>("CurrentPathText"));
    Assert.NotNull(control.FindControl<TextBlock>("ItemCountText"));
}
```

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj --filter "FullyQualifiedName~RemoteFilesSidebarTests" /p:UseAppHost=false /p:OutDir=D:\projects\nova2\.artifacts\sidebar-v3-build\
```

Expected: FAIL because the current XAML does not expose the new host-first header shape.

**Step 3: Write minimal implementation**

- reshape the header into host identity plus compact utility actions
- keep width within the current compact rail target
- add a path row with truncation and item count
- do not introduce transfer-progress UI in the sidebar

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml.cs src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.Tests/Core/RemoteFilesSidebarTests.cs
git commit -m "feat: restyle remote files sidebar header"
```

### Task 4: Convert the listing to a dense two-column layout

**Files:**
- Modify: `src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml`
- Test: `tests/NovaTerminal.Tests/Core/RemoteFilesSidebarTests.cs`

**Step 1: Write the failing test**

Add a UI test that asserts:

- the list row contains a primary name column
- the row contains a secondary modified column
- full-path subtext is not rendered

Example shape:

```csharp
[AvaloniaFact]
public void Sidebar_UsesTwoColumnDenseEntryTemplate()
{
    var control = new RemoteFilesSidebar();

    Assert.NotNull(control.FindControl<Grid>("EntryRowGrid"));
    Assert.Null(control.FindControl<TextBlock>("EntryPathText"));
}
```

**Step 2: Run test to verify it fails**

Run the same command as Task 3 Step 2.

Expected: FAIL because the current template is a simple single-text row.

**Step 3: Write minimal implementation**

- replace the current list item template with a dense two-column layout
- keep directory/file icons simple
- show modified placeholder text when metadata is missing
- preserve keyboard and double-click navigation

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml tests/NovaTerminal.Tests/Core/RemoteFilesSidebarTests.cs
git commit -m "feat: add dense two-column remote sidebar list"
```

### Task 5: Preserve behavior boundaries and refresh docs

**Files:**
- Modify: `docs/USER_MANUAL.md`
- Modify: `docs/plans/2026-05-04-native-sftp-sidebar-v3-design.md`
- Test: `tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs`
- Test: `tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs`

**Step 1: Write the failing test**

Add one regression test that confirms the new visual polish does not change the existing picker-first transfer split.

Example shape:

```csharp
[AvaloniaFact]
public async Task SidebarPolish_DoesNotReintroduceTransferDialogForSidebarDownloads()
{
    var window = new TestMainWindow
    {
        NextSavedLocalFilePath = @"D:\downloads\access.log"
    };

    await window.StartSidebarDownloadForTest(CreateNativeProfile(), Guid.NewGuid(), "/srv/access.log", TransferKind.File);

    Assert.False(window.TransferDialogWasShown);
}
```

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj --filter "FullyQualifiedName~MainWindowTransferFlowTests|FullyQualifiedName~TerminalPaneRemoteFilesSidebarTests" /p:UseAppHost=false /p:OutDir=D:\projects\nova2\.artifacts\sidebar-v3-build\
```

Expected: FAIL if any polish work accidentally reintroduces the old flow.

**Step 3: Write minimal implementation**

- update `docs/USER_MANUAL.md` to describe the refined host-first, modified-aware sidebar
- refresh the design doc if implementation details shifted
- keep the current picker-first transfer flow and pane-local boundaries intact

**Step 4: Run test to verify it passes**

Run the same command as Step 2, then run the broader sidebar slice:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj --filter "FullyQualifiedName~MainWindowTransferFlowTests|FullyQualifiedName~RemoteFilesSidebarTests|FullyQualifiedName~TerminalPaneRemoteFilesSidebarTests|FullyQualifiedName~RemoteFilesSidebarViewModelTests|FullyQualifiedName~RemoteDirectoryBrowserServiceTests" /p:UseAppHost=false /p:OutDir=D:\projects\nova2\.artifacts\sidebar-v3-build\
```

Expected: PASS.

**Step 5: Commit**

```bash
git add docs/USER_MANUAL.md docs/plans/2026-05-04-native-sftp-sidebar-v3-design.md tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs
git commit -m "docs: describe refined native sftp sidebar"
```
