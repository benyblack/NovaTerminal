# Transfer UI Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace NovaTerminal's current SFTP prompt flow with a dedicated transfer dialog, add transfer-history cleanup controls, and make the Transfer Center a movable floating tool surface.

**Architecture:** Keep `SftpService` as the transfer execution boundary and move transfer-input UX into a dedicated Avalonia dialog. The main window will translate entry-point actions into a single transfer-request flow, while the Transfer Center remains a lightweight overlay with better state management and history controls.

**Tech Stack:** Avalonia UI, existing `SftpService` / `TransferJob` models, xUnit, Avalonia.Headless.XUnit

---

### Task 1: Add a dedicated transfer request model and dialog contract

**Files:**
- Create: `src/NovaTerminal.App/Models/TransferDialogRequest.cs`
- Create: `src/NovaTerminal.App/Models/TransferDialogResult.cs`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/TransferDialogRequestTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public void TransferDialogRequest_ForDownloadFile_UsesRemoteDefaults()
{
    TransferDialogRequest request = TransferDialogRequest.ForAction(
        direction: TransferDirection.Download,
        kind: TransferKind.File,
        defaultRemotePath: "~/downloads");

    Assert.Equal(TransferDirection.Download, request.Direction);
    Assert.Equal(TransferKind.File, request.Kind);
    Assert.Equal("~/downloads", request.RemotePath);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~TransferDialogRequestTests" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: FAIL because the request types do not exist yet.

**Step 3: Write minimal implementation**

```csharp
public sealed class TransferDialogRequest
{
    public TransferDirection Direction { get; init; }
    public TransferKind Kind { get; init; }
    public string RemotePath { get; init; } = string.Empty;

    public static TransferDialogRequest ForAction(
        TransferDirection direction,
        TransferKind kind,
        string defaultRemotePath) =>
        new()
        {
            Direction = direction,
            Kind = kind,
            RemotePath = defaultRemotePath
        };
}
```

Add `TransferDialogResult` with local/remote path payload for the dialog return value.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Models/TransferDialogRequest.cs src/NovaTerminal.App/Models/TransferDialogResult.cs tests/NovaTerminal.Tests/Core/TransferDialogRequestTests.cs src/NovaTerminal.App/MainWindow.axaml.cs
git commit -m "feat: add transfer dialog request contract"
```

### Task 2: Add the Transfer Dialog UI and validation

**Files:**
- Create: `src/NovaTerminal.App/Controls/TransferDialog.axaml`
- Create: `src/NovaTerminal.App/Controls/TransferDialog.axaml.cs`
- Modify: `src/NovaTerminal.App/App.axaml` (only if a new converter/resource is required)
- Test: `tests/NovaTerminal.Tests/Core/TransferDialogTests.cs`

**Step 1: Write the failing test**

```csharp
[AvaloniaFact]
public void TransferDialog_DisablesConfirm_WhenRemotePathIsBlank()
{
    var dialog = new TransferDialog(new TransferDialogRequest
    {
        Direction = TransferDirection.Download,
        Kind = TransferKind.File,
        RemotePath = ""
    });

    Button confirm = dialog.FindControl<Button>("BtnTransferConfirm")!;
    Assert.False(confirm.IsEnabled);
}
```

Add a second test to verify that the remote path `TextBox` receives focus on open and that local/remote validation messages are shown inline.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~TransferDialogTests" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: FAIL because the dialog does not exist yet.

**Step 3: Write minimal implementation**

Create a small form surface with:

```xml
<TextBox Name="RemotePathBox" Watermark="~/downloads or /mnt/share" />
<TextBox Name="LocalPathBox" />
<Button Name="BtnBrowseLocal" />
<Button Name="BtnTransferConfirm" />
<TextBlock Name="ValidationMessage" />
```

In code-behind:

```csharp
private void RefreshValidation()
{
    bool valid = !string.IsNullOrWhiteSpace(RemotePathBox.Text)
        && !string.IsNullOrWhiteSpace(LocalPathBox.Text);
    BtnTransferConfirm.IsEnabled = valid;
    ValidationMessage.Text = valid ? string.Empty : "Local and remote paths are required.";
}
```

Ensure the dialog focuses `RemotePathBox` after load and traps Enter/Escape for confirm/cancel.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TransferDialog.axaml src/NovaTerminal.App/Controls/TransferDialog.axaml.cs tests/NovaTerminal.Tests/Core/TransferDialogTests.cs
git commit -m "feat: add transfer dialog ui"
```

### Task 3: Replace the old path-prompt transfer flow with the dialog

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs` (only if entry-point wiring changes)
- Test: `tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task InitiateTransfer_UsesDialogResultAndQueuesJob()
{
    var window = new TestMainWindow();
    window.NextTransferDialogResult = new TransferDialogResult
    {
        LocalPath = @"D:\downloads\a.mkv",
        RemotePath = "/mnt/box/movies/a.mkv"
    };

    await window.InitiateTransferForTest(TransferDirection.Download, TransferKind.File);

    Assert.Single(SftpService.Instance.Jobs);
    Assert.Equal("/mnt/box/movies/a.mkv", SftpService.Instance.Jobs[0].RemotePath);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~MainWindowTransferFlowTests" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: FAIL because the dialog-backed flow does not exist yet.

**Step 3: Write minimal implementation**

- Add a small overridable dialog-launch method in `MainWindow.axaml.cs`
- Replace calls to `PromptForRemotePathAsync(...)` inside `InitiateSftpTransfer(...)`
- Remove the transfer use of `PathPromptOverlay`

Example shape:

```csharp
internal virtual Task<TransferDialogResult?> ShowTransferDialogAsync(TransferDialogRequest request)
{
    var dialog = new TransferDialog(request);
    return dialog.ShowAsync(this);
}
```

Then:

```csharp
TransferDialogResult? result = await ShowTransferDialogAsync(request);
if (result is null) return;
```

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.Tests/Core/MainWindowTransferFlowTests.cs
git commit -m "feat: route transfers through transfer dialog"
```

### Task 4: Add Transfer Center cleanup actions

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TransferCenter.axaml`
- Modify: `src/NovaTerminal.App/Controls/TransferCenter.axaml.cs`
- Modify: `src/NovaTerminal.App/Core/SftpService.cs`
- Test: `tests/NovaTerminal.Tests/Core/SftpServiceTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public void ClearInactiveJobs_RemovesFinishedFailedAndCanceledOnly()
{
    var service = new SftpServiceForTests();
    service.Jobs.Add(new TransferJob { State = TransferState.Running });
    service.Jobs.Add(new TransferJob { State = TransferState.Completed });
    service.Jobs.Add(new TransferJob { State = TransferState.Failed });

    service.ClearInactiveJobs();

    Assert.Single(service.Jobs);
    Assert.Equal(TransferState.Running, service.Jobs[0].State);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~SftpServiceTests.ClearInactiveJobs" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: FAIL because cleanup APIs do not exist yet.

**Step 3: Write minimal implementation**

Add service methods:

```csharp
public void ClearInactiveJobs() { ... }
public void ClearFinishedJobs() { ... }
public void ClearFailedJobs() { ... }
```

Add toolbar buttons in `TransferCenter.axaml`:

```xml
<Button Name="BtnClearFinished" Content="Clear Finished" />
<Button Name="BtnClearFailed" Content="Clear Failed" />
<Button Name="BtnClearInactive" Content="Clear Inactive" />
```

Wire button clicks to the new service methods.

**Step 4: Run test to verify it passes**

Run the same command as Step 2, then the broader `SftpServiceTests` filter.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TransferCenter.axaml src/NovaTerminal.App/Controls/TransferCenter.axaml.cs src/NovaTerminal.App/Core/SftpService.cs tests/NovaTerminal.Tests/Core/SftpServiceTests.cs
git commit -m "feat: add transfer history cleanup actions"
```

### Task 5: Make the Transfer Center movable

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Test: `tests/NovaTerminal.Tests/Core/MainWindowTransferCenterTests.cs`

**Step 1: Write the failing test**

```csharp
[AvaloniaFact]
public void TransferCenterDrag_UpdatesOverlayMargin()
{
    var window = new NovaTerminal.MainWindow();
    Thickness before = window.GetTransferOverlayMarginForTest();

    window.MoveTransferOverlayForTest(new Vector(-120, -40));

    Thickness after = window.GetTransferOverlayMarginForTest();
    Assert.NotEqual(before, after);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~MainWindowTransferCenterTests" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: FAIL because drag behavior and test hooks do not exist yet.

**Step 3: Write minimal implementation**

- Add pointer drag handling to the transfer title bar
- Track overlay margin or translation in `MainWindow`
- Clamp movement within the window bounds if needed

Example shape:

```csharp
private void MoveTransferOverlay(Vector delta)
{
    TransferOverlay.Margin = new Thickness(
        Math.Max(0, TransferOverlay.Margin.Left + delta.X),
        Math.Max(0, TransferOverlay.Margin.Top + delta.Y),
        0,
        0);
}
```

Keep this session-only; do not persist the position in settings in this pass.

**Step 4: Run test to verify it passes**

Run the same command as Step 2.

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.Tests/Core/MainWindowTransferCenterTests.cs
git commit -m "feat: make transfer center movable"
```

### Task 6: Remove the old transfer prompt path and update docs

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Modify: `docs/USER_MANUAL.md`
- Modify: `docs/SSH_ROADMAP.md`

**Step 1: Write the failing test**

For this cleanup task, use a narrow assertion that the transfer flow no longer depends on the old prompt overlay names.

```csharp
[Fact]
public void MainWindow_TransferFlow_NoLongerUsesPathPromptOverlay()
{
    string source = File.ReadAllText("src/NovaTerminal.App/MainWindow.axaml.cs");
    Assert.DoesNotContain("PromptForRemotePathAsync", source, StringComparison.Ordinal);
}
```

**Step 2: Run test to verify it fails**

Run the relevant narrow test command.

Expected: FAIL while the old flow remains.

**Step 3: Write minimal implementation**

- Remove the transfer dependency on `PathPromptOverlay`
- Delete unused transfer-prompt wiring if no longer referenced
- Update docs to describe:
  - transfer dialog as the entry point
  - movable Transfer Center
  - clear-history actions

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c NativeSftpUiFixApp --filter "FullyQualifiedName~TransferDialogTests|FullyQualifiedName~MainWindowTransferFlowTests|FullyQualifiedName~MainWindowTransferCenterTests|FullyQualifiedName~SftpServiceTests" -p:SkipCliShim=true -m:1 --nologo -v:minimal
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml src/NovaTerminal.App/MainWindow.axaml.cs docs/USER_MANUAL.md docs/SSH_ROADMAP.md
git commit -m "docs: update transfer ui workflow"
```
