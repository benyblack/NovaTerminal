# Native SFTP Sidebar V2 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make the Native SSH remote-files sidebar more compact, route sidebar transfers directly through local pickers plus `SftpService`, and remove the old pane-context transfer commands.

**Architecture:** Keep `TerminalPane` as the pane-local sidebar host and event source, keep `MainWindow` as the owner of picker invocation and transfer job creation, and keep `TransferDialog` only for manual command-palette flows. The implementation should remove sidebar dependence on dialog-backed transfer requests without disturbing the existing manual SFTP path.

**Tech Stack:** C#, .NET 10, Avalonia, `SftpService`, xUnit, Avalonia.Headless.XUnit

---

### Task 1: Compact the Remote Files sidebar layout

**Files:**
- Modify: `src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml`
- Modify: `src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/RemoteFilesSidebarTests.cs`

**Step 1: Write the failing test**

Add a UI test that asserts the compact surface shape:

```csharp
[AvaloniaFact]
public void Sidebar_UsesCompactWidth_AndHidesSecondaryPathText()
{
    var control = new RemoteFilesSidebar();

    Assert.Equal(288, control.FindControl<Border>("SidebarChrome")!.Width);
    Assert.Null(control.FindControl<TextBlock>("EntryPathText"));
}
```

Add a second test that asserts the footer exposes `BtnUpload`, `BtnDownload`, and no longer exposes separate `BtnUploadFile` and `BtnUploadFolder` buttons if the implementation consolidates upload into a small menu.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~RemoteFilesSidebarTests" -c Release -m:1 --nologo -v:minimal
```

Expected: FAIL because the current control is still the wider multi-row layout.

**Step 3: Write minimal implementation**

- reduce the sidebar width to the approved compact target
- collapse the header and navigation rows into one compact toolbar
- remove always-visible per-entry full-path text
- reduce footer actions to the approved compact set
- keep keyboard navigation and current bindings intact

If upload remains split into file/folder actions, make those controls visually compact and keep the test aligned with the final chosen control names.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml src/NovaTerminal.App/Controls/RemoteFilesSidebar.axaml.cs tests/NovaTerminal.Tests/Core/RemoteFilesSidebarTests.cs
git commit -m "feat: compact remote files sidebar layout"
```

### Task 2: Route sidebar uploads directly through local pickers

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs`

**Step 1: Write the failing test**

Add tests similar to:

```csharp
[Fact]
public async Task SidebarUploadFile_UsesLocalFilePicker_AndCurrentRemoteDirectory()
{
    var window = new TestMainWindow
    {
        NextPickedLocalFilePath = @"D:\temp\report.txt"
    };

    await window.StartSidebarUploadForTest(
        profile: CreateNativeProfile(),
        sessionId: Guid.NewGuid(),
        remoteDirectory: "/srv/app",
        kind: TransferKind.File);

    Assert.Equal("/srv/app", window.QueuedJob!.RemotePath);
    Assert.Equal(@"D:\temp\report.txt", window.QueuedJob.LocalPath);
    Assert.True(window.FilePickerWasShown);
    Assert.False(window.TransferDialogWasShown);
}
```

Add a second test for folder upload using a local folder picker.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~MainWindowTransferFlowTests.SidebarUpload" -c Release -m:1 --nologo -v:minimal
```

Expected: FAIL because sidebar upload still routes through `TransferDialog`.

**Step 3: Write minimal implementation**

- keep `TerminalPane` sidebar-originated upload events explicit
- in `MainWindow`, replace sidebar upload dialog usage with direct local file or folder picking helpers
- if the picker returns no value, exit without queuing a job
- enqueue the resulting upload job through the existing `SftpService` path

Make the picker entry points `internal virtual` where needed so the tests can override them cleanly.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml.cs src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs
git commit -m "feat: route sidebar uploads through local pickers"
```

### Task 3: Route sidebar downloads directly through save and folder pickers

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs`

**Step 1: Write the failing test**

Add tests similar to:

```csharp
[Fact]
public async Task SidebarDownloadFile_UsesSavePicker_WithRemoteFileName()
{
    var window = new TestMainWindow
    {
        NextSavedLocalFilePath = @"D:\downloads\logs.txt"
    };

    await window.StartSidebarDownloadForTest(
        profile: CreateNativeProfile(),
        sessionId: Guid.NewGuid(),
        selectedRemotePath: "/srv/logs.txt",
        kind: TransferKind.File);

    Assert.Equal("/srv/logs.txt", window.QueuedJob!.RemotePath);
    Assert.Equal(@"D:\downloads\logs.txt", window.QueuedJob.LocalPath);
    Assert.Equal("logs.txt", window.LastSuggestedSaveFileName);
    Assert.True(window.SavePickerWasShown);
    Assert.False(window.TransferDialogWasShown);
}
```

Add a second test for folder download using a local folder picker.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~MainWindowTransferFlowTests.SidebarDownload" -c Release -m:1 --nologo -v:minimal
```

Expected: FAIL because sidebar download still routes through `TransferDialog`.

**Step 3: Write minimal implementation**

- for file download, invoke `SaveFilePicker` with the selected remote filename as the suggested file name
- for folder download, invoke a local folder picker
- if the picker is canceled, do nothing
- queue the download job directly without showing `TransferDialog`

Keep the existing manual SFTP download flow unchanged.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml.cs src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs
git commit -m "feat: route sidebar downloads through local pickers"
```

### Task 4: Remove pane-context SFTP transfer commands and keep manual dialog coverage

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/Models/TransferDialogRequest.cs`
- Modify: `src/NovaTerminal.App/Controls/TransferDialog.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs`
- Test: `tests/NovaTerminal.Tests/Core/TransferDialogRequestTests.cs`

**Step 1: Write the failing test**

Add tests such as:

```csharp
[AvaloniaFact]
public void NativeSshPane_ContextMenu_KeepsOnlyRemoteFilesEntry()
{
    var pane = CreateNativePane();

    IReadOnlyList<string> names = pane.GetSftpContextMenuItemNamesForTest();

    Assert.Equal(new[] { "MenuToggleRemoteFilesSidebar" }, names);
}
```

Add a second test that still verifies manual `TransferDialogRequest.ForAction(...)` keeps the current dialog defaults.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~TerminalPaneRemoteFilesSidebarTests|FullyQualifiedName~TransferDialogRequestTests" -c Release -m:1 --nologo -v:minimal
```

Expected: FAIL because the pane context menu still includes the older transfer commands and sidebar-specific dialog request code still exists.

**Step 3: Write minimal implementation**

- remove the old pane-context upload/download items from the SFTP menu
- keep `Remote Files` as the only pane-context SFTP-related entry
- delete or simplify sidebar-only dialog request plumbing that is no longer used
- keep manual dialog focus/default behavior intact for command-palette flows

If `TransferDialogRequest.ForSidebarAction(...)` becomes dead code, remove it and update tests accordingly.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml src/NovaTerminal.App/Controls/TerminalPane.axaml.cs src/NovaTerminal.App/Models/TransferDialogRequest.cs src/NovaTerminal.App/Controls/TransferDialog.axaml.cs tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs tests/NovaTerminal.Tests/Core/TransferDialogRequestTests.cs
git commit -m "refactor: keep manual sftp dialog flow separate"
```

### Task 5: Refresh docs and run the sidebar transfer regression slice

**Files:**
- Modify: `docs/USER_MANUAL.md`
- Modify: `docs/plans/2026-05-04-native-sftp-sidebar-v2-design.md`
- Test: `tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs`
- Test: `tests/NovaTerminal.Tests/Core/RemoteFilesSidebarTests.cs`
- Test: `tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs`

**Step 1: Write the failing test**

Add one narrow regression asserting the manual and sidebar flows remain distinct:

```csharp
[Fact]
public async Task ManualTransferFlow_StillUsesTransferDialog_WhenSidebarFlowDoesNot()
{
    var window = new TestMainWindow
    {
        NextTransferDialogResult = TransferDialogResult.CreateConfirmed(
            localPath: @"D:\downloads\logs",
            remotePath: "/srv/logs")
    };

    await window.StartManualTransferForTest(CreateNativeProfile(), TransferDirection.Download, TransferKind.Folder);

    Assert.True(window.TransferDialogWasShown);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj --filter "FullyQualifiedName~MainWindowTransferFlowTests|FullyQualifiedName~RemoteFilesSidebarTests|FullyQualifiedName~TerminalPaneRemoteFilesSidebarTests" -c Release -m:1 --nologo -v:minimal
```

Expected: FAIL until the docs and regression surface fully match the new picker-only sidebar flow.

**Step 3: Write minimal implementation**

- update `docs/USER_MANUAL.md` to describe the compact sidebar as the primary Native SSH transfer path
- note that manual SFTP commands remain available in the command palette
- refresh the design doc if implementation details changed slightly
- make any final test-harness cleanup needed for the picker-based flow

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add docs/USER_MANUAL.md docs/plans/2026-05-04-native-sftp-sidebar-v2-design.md tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs tests/NovaTerminal.Tests/Core/RemoteFilesSidebarTests.cs tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs
git commit -m "docs: describe compact picker-first sftp sidebar"
```
