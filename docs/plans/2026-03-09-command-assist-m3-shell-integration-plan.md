# Command Assist M3 Shell Integration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add PowerShell-first shell integration for Command Assist so command lifecycle, cwd, exit code, and multiline capture can come from structured shell events with graceful fallback to the current heuristic path.

**Architecture:** Add an App-layer shell integration subsystem that selects a provider at pane launch time, generates a PowerShell bootstrap script, consumes minimal parser lifecycle markers through a shell lifecycle tracker, and feeds normalized structured events into Command Assist. Keep VT/parser changes additive and narrow, remove PowerShell-specific startup behavior from the PTY layer, and preserve heuristic capture for unsupported or failed integrations.

**Tech Stack:** C#, Avalonia, existing `AnsiParser` OSC handling, `TerminalPane`, `CommandAssistController`, xUnit, Headless Avalonia tests, .NET 10

---

### Task 1: Add failing shell integration contract tests

**Files:**
- Create: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationRegistryTests.cs`
- Create: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellShellIntegrationProviderTests.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Contracts/IShellIntegrationProvider.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Contracts/ShellIntegrationLaunchPlan.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Runtime/ShellIntegrationRegistry.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public void GetProvider_ForPwshProfile_ReturnsPowerShellProvider()
{
    var registry = new ShellIntegrationRegistry(new IShellIntegrationProvider[]
    {
        new PowerShellShellIntegrationProvider()
    });

    IShellIntegrationProvider? provider = registry.GetProvider(
        shellKind: "pwsh",
        profile: new TerminalProfile { Command = "pwsh.exe" });

    Assert.NotNull(provider);
}

[Fact]
public void CreateLaunchPlan_WhenPowerShellEnabled_ReturnsIntegratedPlan()
{
    var provider = new PowerShellShellIntegrationProvider();

    ShellIntegrationLaunchPlan plan = provider.CreateLaunchPlan(
        shellCommand: "pwsh.exe",
        shellArguments: "-NoLogo",
        workingDirectory: @"C:\repo");

    Assert.True(plan.IsIntegrated);
    Assert.NotNull(plan.BootstrapScriptPath);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "ShellIntegrationRegistryTests|PowerShellShellIntegrationProviderTests" --logger "console;verbosity=minimal"`

Expected: FAIL with missing shell integration contracts and provider types.

**Step 3: Write minimal implementation**

Add the smallest contract types:

```csharp
public interface IShellIntegrationProvider
{
    bool CanIntegrate(string? shellKind, TerminalProfile? profile);
    ShellIntegrationLaunchPlan CreateLaunchPlan(string shellCommand, string? shellArguments, string? workingDirectory);
}

public sealed record ShellIntegrationLaunchPlan(
    bool IsIntegrated,
    string ShellCommand,
    string? ShellArguments,
    string? BootstrapScriptPath);
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "ShellIntegrationRegistryTests|PowerShellShellIntegrationProviderTests" --logger "console;verbosity=minimal"`

Expected: PASS

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist/ShellIntegration src/NovaTerminal.App/CommandAssist/ShellIntegration
git commit -m "Add shell integration provider contracts"
```

### Task 2: Add failing PowerShell bootstrap builder tests

**Files:**
- Create: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellBootstrapBuilderTests.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Assets/PowerShell/CommandAssistBootstrap.ps1`
- Modify: `src/NovaTerminal.App/Core/AppPaths.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public void BuildBootstrapScript_ContainsOsc133LifecycleMarkers()
{
    string script = PowerShellBootstrapBuilder.BuildScript();

    Assert.Contains("]133;B", script);
    Assert.Contains("]133;D;", script);
    Assert.Contains("]7;", script);
}

[Fact]
public void WriteBootstrapScript_WritesIntoAppPath()
{
    string path = PowerShellBootstrapBuilder.WriteScript(AppPaths.CommandAssistDirectory);

    Assert.True(File.Exists(path));
    Assert.EndsWith(".ps1", path, StringComparison.OrdinalIgnoreCase);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "PowerShellBootstrapBuilderTests" --logger "console;verbosity=minimal"`

Expected: FAIL with missing bootstrap builder and asset handling.

**Step 3: Write minimal implementation**

Use a generated script that emits:

```powershell
$esc = [char]27
$bel = [char]7
function Write-NovaOsc([string]$payload) {
    [Console]::Out.Write("$esc]$payload$bel")
}
```

Include hooks for:

- prompt ready
- cwd update
- command started
- command finished with exit code

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "PowerShellBootstrapBuilderTests" --logger "console;verbosity=minimal"`

Expected: PASS

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellBootstrapBuilderTests.cs src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell src/NovaTerminal.App/Core/AppPaths.cs
git commit -m "Add PowerShell shell integration bootstrap builder"
```

### Task 3: Add failing parser tests for any new lifecycle markers

**Files:**
- Modify: `tests/NovaTerminal.Tests/OscShellIntegrationTests.cs`
- Modify: `src/NovaTerminal.VT/AnsiParser.cs`

**Step 1: Write the failing tests**

If M3 needs prompt-ready or command-accepted markers beyond existing `OSC 133;B/D`, add targeted tests only:

```csharp
[Fact]
public void Osc133A_RaisesPromptReady()
{
    var buffer = new TerminalBuffer(80, 24);
    var parser = new AnsiParser(buffer);
    bool fired = false;

    parser.OnPromptReady += () => fired = true;
    parser.Process("\x1b]133;A\x07");

    Assert.True(fired);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "OscShellIntegrationTests" --logger "console;verbosity=minimal"`

Expected: FAIL only for newly added marker expectations.

**Step 3: Write minimal implementation**

Extend `AnsiParser` narrowly:

```csharp
public Action? OnPromptReady { get; set; }
```

Parse only the exact additional OSC markers M3 requires.

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "OscShellIntegrationTests" --logger "console;verbosity=minimal"`

Expected: PASS

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/OscShellIntegrationTests.cs src/NovaTerminal.VT/AnsiParser.cs
git commit -m "Add minimal OSC shell lifecycle markers for M3"
```

### Task 4: Add failing shell lifecycle tracker tests

**Files:**
- Create: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellLifecycleTrackerTests.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Runtime/ShellIntegrationEvent.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Runtime/ShellIntegrationEventType.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Runtime/ShellLifecycleTracker.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public void CommandStarted_ThenFinished_ProducesCompletedEventWithExitCode()
{
    var tracker = new ShellLifecycleTracker();

    tracker.HandleCommandStarted();
    ShellIntegrationEvent completed = tracker.HandleCommandFinished(17)!;

    Assert.Equal(ShellIntegrationEventType.CommandCompleted, completed.Type);
    Assert.Equal(17, completed.ExitCode);
}

[Fact]
public void WorkingDirectoryUpdate_ProducesCwdChangedEvent()
{
    var tracker = new ShellLifecycleTracker();

    ShellIntegrationEvent changed = tracker.HandleWorkingDirectoryChanged(@"C:\repo")!;

    Assert.Equal(ShellIntegrationEventType.CwdChanged, changed.Type);
    Assert.Equal(@"C:\repo", changed.WorkingDirectory);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "ShellLifecycleTrackerTests" --logger "console;verbosity=minimal"`

Expected: FAIL with missing lifecycle tracker types.

**Step 3: Write minimal implementation**

Normalize parser callbacks into a tracker that can hold:

- last prompt-ready time
- current cwd
- current command start time
- current accepted command state

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "ShellLifecycleTrackerTests" --logger "console;verbosity=minimal"`

Expected: PASS

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellLifecycleTrackerTests.cs src/NovaTerminal.App/CommandAssist/ShellIntegration/Runtime
git commit -m "Add shell lifecycle tracker"
```

### Task 5: Add failing structured Command Assist controller tests

**Files:**
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Models/CommandCaptureSource.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Models/CommandHistoryEntry.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public async Task HandleStructuredCommandCompleted_PersistsShellIntegrationEntry()
{
    var historyStore = new InMemoryHistoryStore();
    var controller = CreateController(historyStore);

    await controller.HandleStructuredCommandCompletedAsync(
        commandText: "git status",
        workingDirectory: @"C:\repo",
        exitCode: 0,
        durationMs: 420);

    Assert.Single(historyStore.Entries);
    Assert.Equal(CommandCaptureSource.ShellIntegration, historyStore.Entries[0].Source);
    Assert.Equal(420, historyStore.Entries[0].DurationMs);
}

[Fact]
public async Task HandleEnterAsync_WhenShellIntegrationActive_DoesNotPersistHeuristicEntry()
{
    var historyStore = new InMemoryHistoryStore();
    var controller = CreateController(historyStore);
    controller.SetShellIntegrationActive(true);
    controller.HandleTextInput("git status");

    await controller.HandleEnterAsync();

    Assert.Empty(historyStore.Entries);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests" --logger "console;verbosity=minimal"`

Expected: FAIL on missing structured lifecycle handling and capture-source enum value.

**Step 3: Write minimal implementation**

Add:

```csharp
public enum CommandCaptureSource
{
    Heuristic,
    ShellIntegration
}
```

And extend `CommandHistoryEntry`:

```csharp
long? DurationMs
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests" --logger "console;verbosity=minimal"`

Expected: PASS

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs src/NovaTerminal.App/CommandAssist/Models
git commit -m "Add structured lifecycle capture to Command Assist controller"
```

### Task 6: Add failing pane integration tests and wire lifecycle into TerminalPane

**Files:**
- Create: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/TerminalPaneShellIntegrationTests.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public void ParserCommandFinished_WhenIntegrationActive_ForwardsStructuredLifecycle()
{
    var pane = new TerminalPane();
    // Create focused seam test around an internal helper or tracker adapter.

    Assert.True(/* controller received structured completion */);
}
```

If direct `TerminalPane` testing is too heavy, push the seam into a small adapter class and test that instead.

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "TerminalPaneShellIntegrationTests" --logger "console;verbosity=minimal"`

Expected: FAIL with missing lifecycle wiring seam.

**Step 3: Write minimal implementation**

Wire `TerminalPane` to:

- obtain provider via `CommandAssistInfrastructure`
- create a launch plan before session startup
- feed parser callbacks into `ShellLifecycleTracker`
- call new controller structured handlers when integration is active

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "TerminalPaneShellIntegrationTests" --logger "console;verbosity=minimal"`

Expected: PASS

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/TerminalPaneShellIntegrationTests.cs src/NovaTerminal.App/Controls/TerminalPane.axaml.cs src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs
git commit -m "Wire shell lifecycle integration into TerminalPane"
```

### Task 7: Remove PTY-layer PowerShell special casing and move launch behavior behind provider

**Files:**
- Modify: `src/NovaTerminal.Pty/RustPtySession.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellShellIntegrationProviderTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public void RustPtySession_DoesNotOwnPowerShellIntegrationBootstrap()
{
    string source = File.ReadAllText(@"src/NovaTerminal.Pty/RustPtySession.cs");

    Assert.DoesNotContain("nova_init_", source);
}
```

If source-text tests are too brittle, replace this with a behavioral test around launch planning and input injection responsibility.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "PowerShellShellIntegrationProviderTests" --logger "console;verbosity=minimal"`

Expected: FAIL because PTY still contains PowerShell-specific bootstrap behavior.

**Step 3: Write minimal implementation**

- remove or disable ad hoc PowerShell bootstrap injection from `RustPtySession`
- keep generic shell launch behavior only
- pass integration-specific startup through the App-layer launch plan

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "PowerShellShellIntegrationProviderTests" --logger "console;verbosity=minimal"`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.Pty/RustPtySession.cs src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellShellIntegrationProviderTests.cs
git commit -m "Move PowerShell shell integration out of PTY layer"
```

### Task 8: Add settings and persistence hooks for shell integration toggles

**Files:**
- Modify: `src/NovaTerminal.App/Core/TerminalSettings.cs`
- Modify: `src/NovaTerminal.App/Core/AppJsonContext.cs`
- Modify: `tests/NovaTerminal.Tests/Core` or create `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationSettingsTests.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public void TerminalSettings_DefaultsEnableShellIntegration()
{
    var settings = new TerminalSettings();

    Assert.True(settings.CommandAssistShellIntegrationEnabled);
    Assert.True(settings.CommandAssistPowerShellIntegrationEnabled);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "ShellIntegrationSettingsTests" --logger "console;verbosity=minimal"`

Expected: FAIL with missing settings properties.

**Step 3: Write minimal implementation**

Add:

```csharp
public bool CommandAssistShellIntegrationEnabled { get; set; } = true;
public bool CommandAssistPowerShellIntegrationEnabled { get; set; } = true;
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "ShellIntegrationSettingsTests" --logger "console;verbosity=minimal"`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/TerminalSettings.cs src/NovaTerminal.App/Core/AppJsonContext.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration
git commit -m "Add shell integration settings defaults"
```

### Task 9: Add fallback and duplicate-suppression tests

**Files:**
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`
- Create or modify: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationFallbackTests.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public async Task StructuredCommandAndHeuristicEnter_DoNotPersistDuplicateHistoryEntries()
{
    var historyStore = new InMemoryHistoryStore();
    var controller = CreateController(historyStore);
    controller.SetShellIntegrationActive(true);
    controller.HandleTextInput("git status");

    await controller.HandleEnterAsync();
    await controller.HandleStructuredCommandCompletedAsync("git status", @"C:\repo", 0, 100);

    Assert.Single(historyStore.Entries);
}

[Fact]
public async Task MissingMarkers_FallsBackToHeuristicCapture()
{
    var historyStore = new InMemoryHistoryStore();
    var controller = CreateController(historyStore);
    controller.SetShellIntegrationActive(false);
    controller.HandleTextInput("git status");

    await controller.HandleEnterAsync();

    Assert.Single(historyStore.Entries);
    Assert.Equal(CommandCaptureSource.Heuristic, historyStore.Entries[0].Source);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "ShellIntegrationFallbackTests|CommandAssistControllerTests" --logger "console;verbosity=minimal"`

Expected: FAIL on duplicate suppression and fallback policy gaps.

**Step 3: Write minimal implementation**

Implement:

- heuristic capture gating when structured integration is active
- structured-to-heuristic fallback when no integration markers are observed
- duplicate suppression keyed by recent command identity and timestamp

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "ShellIntegrationFallbackTests|CommandAssistControllerTests" --logger "console;verbosity=minimal"`

Expected: PASS

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs
git commit -m "Add shell integration fallback and duplicate suppression"
```

### Task 10: Final targeted verification and docs notes

**Files:**
- Modify: `docs/plans/2026-03-09-command-assist-m3-shell-integration-design.md` only if implementation decisions materially differ
- Optionally add: `docs/command-assist/PowerShell_Integration_Setup.md`

**Step 1: Run the targeted verification suite**

Run:

```bash
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "OscShellIntegrationTests|CommandAssistControllerTests|CommandAssistSuggestionEngineTests|AlternateScreenTests|HeadlessUITests|ShellIntegration" --logger "console;verbosity=minimal"
```

Expected: PASS

**Step 2: Run a Release compile**

Run:

```bash
dotnet msbuild src\NovaTerminal.App\NovaTerminal.App.csproj /t:Compile /p:Configuration=Release /v:minimal
```

Expected: success

**Step 3: Document known gaps**

Write short notes for:

- PowerShell setup expectations
- current fallback semantics
- known gaps for bash/zsh/fish

**Step 4: Commit**

```bash
git add docs src tests
git commit -m "Document Command Assist M3 PowerShell integration"
```

**Step 5: Prepare PR summary**

Include:

- PowerShell-first structured lifecycle
- heuristic fallback preserved
- exact tests run

---

## Notes for Execution

- Prefer `Release` test runs in this repo when local debug artifacts are locked.
- Do not start by modifying `AnsiParser` until the provider and tracker tests show exactly which extra markers are required.
- If the chosen PowerShell marker design can fit entirely within existing `OSC 7` and `OSC 133;B/D`, skip additional parser events.
- Keep every shell-specific decision in the new shell integration subsystem unless there is a compelling reason otherwise.

## Recommended First Batch for `executing-plans`

1. Task 1: shell integration contract tests
2. Task 2: PowerShell bootstrap builder tests
3. Task 4: shell lifecycle tracker tests

That batch will define the App-layer shell integration shape before any launch-path or parser behavior changes.
