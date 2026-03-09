# Command Assist M3.1 Closeout Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Close the remaining Command Assist M3 acceptance gaps by preserving PowerShell prompt behavior, tightening structured command completion, adding multiline shell-integration coverage, and committing the missing integration docs.

**Architecture:** Keep M3.1 as an additive hardening pass inside the existing App-layer shell integration subsystem. The PowerShell bootstrap remains the integration entrypoint, but it must wrap the user's prompt instead of replacing it, and it must only emit command-finished markers when an accepted command is active.

**Tech Stack:** C#, xUnit, Avalonia app layer, PowerShell bootstrap script generation, existing OSC 133 shell-integration parsing.

---

### Task 1: Add failing prompt-preservation tests

**Files:**
- Modify: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellBootstrapBuilderTests.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs`

**Step 1: Write the failing test**

Add tests that assert the generated bootstrap script:
- does not hardcode `'PS ' + (Get-Location) + '> '`
- contains logic to preserve or invoke the existing prompt implementation

Example assertions:

```csharp
[Fact]
public void BuildScript_DoesNotHardcodeDefaultPromptRendering()
{
    string script = PowerShellBootstrapBuilder.BuildScript();

    Assert.DoesNotContain("'PS ' + (Get-Location) + '> '", script);
}

[Fact]
public void BuildScript_WrapsExistingPromptImplementation()
{
    string script = PowerShellBootstrapBuilder.BuildScript();

    Assert.Contains("Get-Command prompt", script);
    Assert.Contains("& $script:NovaOriginalPrompt", script);
}
```

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~PowerShellBootstrapBuilderTests"
```

Expected: FAIL because the current script still hardcodes the prompt body.

**Step 3: Write minimal implementation**

Update `src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs` so the generated script:
- captures the original `prompt` command or scriptblock
- wraps it in a new `prompt` function
- emits Nova markers before calling the original prompt
- returns the original prompt output

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~PowerShellBootstrapBuilderTests"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellBootstrapBuilderTests.cs src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs
git commit -m "Harden PowerShell prompt preservation for Command Assist"
```

### Task 2: Add failing trustworthy-completion tests

**Files:**
- Modify: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellLifecycleTrackerTests.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`

**Step 1: Write the failing test**

Add controller or lifecycle tests that assert:
- no completion enrichment occurs when only a prompt-ready event has happened
- completion without prior accepted command does not mutate history
- accepted command followed by finished event still updates exit code and duration

Example assertions:

```csharp
[Fact]
public async Task HandleShellIntegrationEventAsync_WhenFinishedWithoutAcceptedCommand_DoesNotPatchHistory()
{
    var historyStore = new TestHistoryStore();
    var controller = CreateController(historyStore);
    controller.SetShellIntegrationEnabled(true);

    await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
        ShellIntegrationEventType.CommandFinished,
        CommandText: null,
        WorkingDirectory: @"C:\repo",
        ExitCode: 1,
        DurationMs: 500,
        TimestampUtc: DateTimeOffset.UtcNow));

    Assert.Empty(historyStore.Entries);
}
```

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~HandleShellIntegrationEventAsync_WhenFinishedWithoutAcceptedCommand_DoesNotPatchHistory"
```

Expected: FAIL if the current lifecycle still permits synthetic completion handling.

**Step 3: Write minimal implementation**

Update the PowerShell bootstrap and any related controller assumptions so finish handling requires prior accepted-command state.

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistControllerTests|FullyQualifiedName~ShellLifecycleTrackerTests"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellLifecycleTrackerTests.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs
git commit -m "Require accepted commands before completion markers"
```

### Task 3: Add failing multiline shell-integration tests

**Files:**
- Modify: `tests/NovaTerminal.Tests/OscShellIntegrationTests.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`

**Step 1: Write the failing test**

Add tests that assert:
- `OSC 133;C` decodes multiline base64 payloads correctly
- a multiline structured command is persisted as one history entry

Example parser assertion:

```csharp
[Fact]
public void Osc133C_WithMultilineBase64Command_RaisesCommandAccepted()
{
    var buffer = new TerminalBuffer(80, 24);
    var parser = new AnsiParser(buffer);
    string? accepted = null;

    parser.OnCommandAccepted += command => accepted = command;
    parser.Process("\x1b]133;C;Zm9yZWFjaCAoJGkgaW4gMS4uMykgewpwdXQgJGkKfQ==\x07");

    Assert.Contains("\n", accepted);
}
```

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~Osc133C_WithMultilineBase64Command_RaisesCommandAccepted"
```

Expected: FAIL if multiline structured handling is not yet explicitly covered or preserved end-to-end.

**Step 3: Write minimal implementation**

If needed, adjust controller persistence or parser handling so multiline accepted commands survive intact as one history entry.

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~OscShellIntegrationTests|FullyQualifiedName~CommandAssistControllerTests"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/OscShellIntegrationTests.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs
git commit -m "Add multiline shell integration coverage"
```

### Task 4: Implement the PowerShell bootstrap hardening

**Files:**
- Modify: `src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellBootstrapBuilderTests.cs`

**Step 1: Review the current bootstrap script generation**

Inspect:
- prompt override logic
- PSReadLine `AddToHistoryHandler`
- `PowerShell.OnIdle` completion logic

**Step 2: Implement minimal hardening**

Change the generated script to:
- preserve the original prompt rendering
- keep cwd and prompt-ready markers additive
- emit finish markers only when a command has actually been accepted
- clear command state after a finish marker

**Step 3: Run focused tests**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~PowerShellBootstrapBuilderTests|FullyQualifiedName~OscShellIntegrationTests|FullyQualifiedName~CommandAssistControllerTests"
```

Expected: PASS.

**Step 4: Run broader regression checks**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~PowerShell|FullyQualifiedName~ReflowRegressionTests|FullyQualifiedName~OscShellIntegrationTests|FullyQualifiedName~CommandAssistControllerTests"
```

Expected: PASS with no PowerShell prompt/cursor regressions.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellBootstrapBuilderTests.cs tests/NovaTerminal.Tests/OscShellIntegrationTests.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs
git commit -m "Close PowerShell shell integration lifecycle gaps"
```

### Task 5: Add committed PowerShell integration docs

**Files:**
- Create: `docs/command-assist/CommandAssist_PowerShell_Integration.md`
- Create: `docs/command-assist/CommandAssist_ShellIntegration_Gaps.md`

**Step 1: Write the docs**

`CommandAssist_PowerShell_Integration.md` should cover:
- what M3 integrates today
- how PowerShell integration is activated
- marker flow: cwd, prompt ready, accepted, started, finished
- fallback behavior

`CommandAssist_ShellIntegration_Gaps.md` should cover:
- not yet implemented: bash, zsh, fish
- current PowerShell limitations
- deferred follow-up items

**Step 2: Verify docs against the prompt pack**

Check against:
- `docs/command-assist/CommandAssist.md`
- `docs/command-assist/CommandAssist_PromptPack.md`

Expected: required M3 outputs are now present in tracked docs.

**Step 3: Commit**

```bash
git add docs/command-assist/CommandAssist_PowerShell_Integration.md docs/command-assist/CommandAssist_ShellIntegration_Gaps.md
git commit -m "Document Command Assist PowerShell integration"
```

### Task 6: Final verification

**Files:**
- Verify: `src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs`
- Verify: `tests/NovaTerminal.Tests/OscShellIntegrationTests.cs`
- Verify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`
- Verify: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellBootstrapBuilderTests.cs`
- Verify: `docs/command-assist/CommandAssist_PowerShell_Integration.md`
- Verify: `docs/command-assist/CommandAssist_ShellIntegration_Gaps.md`

**Step 1: Run the targeted M3.1 suite**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~OscShellIntegrationTests|FullyQualifiedName~CommandAssistControllerTests|FullyQualifiedName~PowerShellBootstrapBuilderTests|FullyQualifiedName~ShellLifecycleTrackerTests"
```

Expected: PASS.

**Step 2: Run PowerShell-related regressions**

Run:

```powershell
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~PowerShell|FullyQualifiedName~ReflowRegressionTests"
```

Expected: PASS.

**Step 3: Inspect git status**

Run:

```bash
git status --short
```

Expected: only the intended M3.1 code and docs are staged or modified.

**Step 4: Commit final cleanups if needed**

```bash
git add .
git commit -m "Complete Command Assist M3.1 closeout"
```
