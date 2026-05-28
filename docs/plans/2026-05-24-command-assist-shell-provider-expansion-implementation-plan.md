# Command Assist Shell Provider Expansion Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Finish the PowerShell shell-integration contract and then expand Command Assist shell integration to `bash`, `zsh`, and `fish` using the existing provider model without coupling UI or terminal-core logic.

**Architecture:** Keep shell integration as an App-layer subsystem rooted in `TerminalPane`, `CommandAssistController`, and the `IShellIntegrationProvider` registry. Treat PowerShell as the reference implementation for the full lifecycle contract, then add one provider at a time for `bash`, `zsh`, and `fish`, reusing the existing OSC 7 / OSC 133 parser surface and preserving heuristic fallback for unsupported or failed integration paths.

**Tech Stack:** C#, .NET 10, Avalonia, `AnsiParser`, `TerminalPane`, `CommandAssistController`, xUnit, Headless Avalonia tests, generated shell bootstrap scripts

---

### Task 1: Close the PowerShell accepted-command gap

**Files:**
- Modify: `src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellBootstrapBuilderTests.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`
- Test: `tests/NovaTerminal.Tests/OscShellIntegrationTests.cs`

**Step 1: Write the failing tests**

Add focused tests that assert:
- the generated PowerShell bootstrap script emits `OSC 133;C;<base64-command>`
- structured accepted-command events create history entries with `CommandCaptureSource.ShellIntegration`
- structured finished events patch exit code and duration onto the same entry
- multiline accepted-command payloads survive decoding intact

Example test targets:

```csharp
[Fact]
public void BuildScript_EmitsOsc133AcceptedCommandMarker()
{
    string script = PowerShellBootstrapBuilder.BuildScript();

    Assert.Contains("]133;C;", script);
}

[Fact]
public async Task HandleShellIntegrationEventAsync_WhenAcceptedThenFinished_PersistsAndPatchesStructuredHistory()
{
    var historyStore = new InMemoryHistoryStore();
    var controller = CreateController(historyStore: historyStore);
    controller.SetShellIntegrationEnabled(true);

    await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
        ShellIntegrationEventType.CommandAccepted,
        DateTimeOffset.UtcNow,
        "git status",
        @"C:\repo",
        null,
        null));

    await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
        ShellIntegrationEventType.CommandFinished,
        DateTimeOffset.UtcNow.AddSeconds(1),
        null,
        @"C:\repo",
        0,
        TimeSpan.FromSeconds(1)));

    Assert.Contains(historyStore.Entries, e => e.Source == CommandCaptureSource.ShellIntegration && e.ExitCode == 0);
}
```

**Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~PowerShellBootstrapBuilderTests|FullyQualifiedName~CommandAssistControllerTests|FullyQualifiedName~OscShellIntegrationTests"
```

Expected: FAIL because the current PowerShell bootstrap emits `A` and `D`, but not `C`, and the controller path is still hybrid.

**Step 3: Write minimal implementation**

Update the generated PowerShell script so it:
- captures the accepted command text at the shell boundary
- base64-encodes the command text
- emits `OSC 133;C;<payload>` only when a real command is accepted
- keeps prompt preservation intact
- clears tracked command state after the corresponding `D` marker

Keep controller changes minimal:
- continue using `HandleShellIntegratedCommandAcceptedAsync`
- continue using `HandleShellIntegratedCommandFinishedAsync`
- do not regress heuristic fallback when structured markers are absent

**Step 4: Run tests to verify they pass**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~PowerShellBootstrapBuilderTests|FullyQualifiedName~CommandAssistControllerTests|FullyQualifiedName~OscShellIntegrationTests"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/ShellIntegration/PowerShell/PowerShellBootstrapBuilder.cs src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/PowerShellBootstrapBuilderTests.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs tests/NovaTerminal.Tests/OscShellIntegrationTests.cs
git commit -m "Complete structured PowerShell command capture for Command Assist"
```

### Task 2: Split shell-kind detection into real shell identities

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationRegistryTests.cs`

**Step 1: Write the failing tests**

Add tests that assert:
- `pwsh` returns `pwsh`
- `/bin/bash` and `bash.exe` return `bash`
- `zsh` returns `zsh`
- `fish` returns `fish`
- generic `sh` returns `sh`

Example assertions:

```csharp
[Theory]
[InlineData("pwsh.exe", "pwsh")]
[InlineData("/bin/bash", "bash")]
[InlineData("zsh", "zsh")]
[InlineData("fish", "fish")]
[InlineData("/bin/sh", "sh")]
public void DetermineShellKind_ReturnsSpecificShellKinds(string shellCommand, string expected)
{
    string actual = InvokeDetermineShellKind(shellCommand);
    Assert.Equal(expected, actual);
}
```

**Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~ShellIntegrationRegistryTests|FullyQualifiedName~DetermineShellKind"
```

Expected: FAIL because current detection collapses `bash`, `zsh`, and `sh` into `posix`.

**Step 3: Write minimal implementation**

Update `DetermineShellKind` so it returns:
- `pwsh`
- `cmd`
- `bash`
- `zsh`
- `fish`
- `sh`
- `unknown`

Do not broaden behavior beyond shell identity. Leave launch-plan selection and ranking behavior unchanged in this task.

**Step 4: Run tests to verify they pass**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~ShellIntegrationRegistryTests|FullyQualifiedName~DetermineShellKind"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationRegistryTests.cs
git commit -m "Split Command Assist shell detection by shell type"
```

### Task 3: Add failing Bash provider contract tests

**Files:**
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Bash/BashShellIntegrationProvider.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Bash/BashBootstrapBuilder.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/BashShellIntegrationProviderTests.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/BashBootstrapBuilderTests.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationRegistryTests.cs`

**Step 1: Write the failing tests**

Add tests that assert:
- the registry returns a Bash provider for shell kind `bash`
- the launch plan marks Bash as integrated
- the bootstrap script contains `OSC 7`, `OSC 133;A`, `OSC 133;C`, and `OSC 133;D`
- existing user arguments are preserved

Example assertions:

```csharp
[Fact]
public void GetProvider_ForBashProfile_ReturnsBashProvider()
{
    var registry = CommandAssistInfrastructure.GetShellIntegrationRegistry();
    IShellIntegrationProvider? provider = registry.GetProvider("bash", new TerminalProfile { Command = "bash.exe" });
    Assert.IsType<BashShellIntegrationProvider>(provider);
}

[Fact]
public void BuildScript_ContainsStructuredLifecycleMarkers()
{
    string script = BashBootstrapBuilder.BuildScript();

    Assert.Contains("]7;", script);
    Assert.Contains("]133;A", script);
    Assert.Contains("]133;C;", script);
    Assert.Contains("]133;D;", script);
}
```

**Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~BashShellIntegrationProviderTests|FullyQualifiedName~BashBootstrapBuilderTests|FullyQualifiedName~ShellIntegrationRegistryTests"
```

Expected: FAIL with missing provider and bootstrap types.

**Step 3: Write minimal implementation**

Create:
- `BashShellIntegrationProvider` implementing `IShellIntegrationProvider`
- `BashBootstrapBuilder` generating a Bash bootstrap script under the Command Assist app path

Keep the launch-plan shape identical to PowerShell:
- preserve original shell command
- preserve user arguments
- prepend or append the minimal bootstrap command needed for integration
- return `IsIntegrated = false` when the user already forces an incompatible startup script mode

**Step 4: Run tests to verify they pass**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~BashShellIntegrationProviderTests|FullyQualifiedName~BashBootstrapBuilderTests|FullyQualifiedName~ShellIntegrationRegistryTests"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/ShellIntegration/Bash src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/BashShellIntegrationProviderTests.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/BashBootstrapBuilderTests.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationRegistryTests.cs
git commit -m "Add Bash shell integration provider for Command Assist"
```

### Task 4: Validate Bash end-to-end through parser, tracker, and controller

**Files:**
- Modify: `tests/NovaTerminal.Tests/OscShellIntegrationTests.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellLifecycleTrackerTests.cs`

**Step 1: Write the failing tests**

Add tests that assert:
- Bash accepted-command payloads decode through `AnsiParser`
- Bash structured events flow through `ShellLifecycleTracker`
- controller persistence remains one-entry-per-accepted-command
- multiline Bash commands persist as a single history row

Example parser test:

```csharp
[Fact]
public void Osc133C_WithBase64Command_RaisesAcceptedCommand()
{
    var buffer = new TerminalBuffer(80, 24);
    var parser = new AnsiParser(buffer);
    string? accepted = null;

    parser.OnCommandAccepted += text => accepted = text;
    parser.Process("\x1b]133;C;Z2l0IHN0YXR1cw==\x07");

    Assert.Equal("git status", accepted);
}
```

**Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~OscShellIntegrationTests|FullyQualifiedName~CommandAssistControllerTests|FullyQualifiedName~ShellLifecycleTrackerTests"
```

Expected: FAIL if Bash-specific accepted-command scenarios are not yet covered end to end.

**Step 3: Write minimal implementation**

If any failures expose contract issues:
- fix only the provider/bootstrap output
- do not special-case Bash inside `CommandAssistController`
- keep parser changes additive only if a real gap is exposed

**Step 4: Run tests to verify they pass**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~OscShellIntegrationTests|FullyQualifiedName~CommandAssistControllerTests|FullyQualifiedName~ShellLifecycleTrackerTests"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/OscShellIntegrationTests.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellLifecycleTrackerTests.cs
git commit -m "Validate Bash shell integration lifecycle end to end"
```

### Task 5: Add failing Zsh provider contract tests

**Files:**
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Zsh/ZshShellIntegrationProvider.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Zsh/ZshBootstrapBuilder.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ZshShellIntegrationProviderTests.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ZshBootstrapBuilderTests.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationRegistryTests.cs`

**Step 1: Write the failing tests**

Add tests that assert:
- the registry returns a Zsh provider for shell kind `zsh`
- the bootstrap script contains `OSC 7`, `OSC 133;A`, `OSC 133;C`, and `OSC 133;D`
- the script is structured to preserve the user prompt instead of replacing it wholesale

Example assertions:

```csharp
[Fact]
public void GetProvider_ForZshProfile_ReturnsZshProvider()
{
    var registry = CommandAssistInfrastructure.GetShellIntegrationRegistry();
    IShellIntegrationProvider? provider = registry.GetProvider("zsh", new TerminalProfile { Command = "zsh" });
    Assert.IsType<ZshShellIntegrationProvider>(provider);
}

[Fact]
public void BuildScript_PreservesPromptOwnership()
{
    string script = ZshBootstrapBuilder.BuildScript();

    Assert.Contains("]133;A", script);
    Assert.Contains("]133;C;", script);
    Assert.DoesNotContain("PS ", script);
}
```

**Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~ZshShellIntegrationProviderTests|FullyQualifiedName~ZshBootstrapBuilderTests|FullyQualifiedName~ShellIntegrationRegistryTests"
```

Expected: FAIL with missing Zsh provider types.

**Step 3: Write minimal implementation**

Create:
- `ZshShellIntegrationProvider`
- `ZshBootstrapBuilder`

Scope for this task:
- local startup only
- same normalized lifecycle markers as Bash
- prompt-preservation-first design

Do not add Zsh-specific ranking, docs, or UI behavior here.

**Step 4: Run tests to verify they pass**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~ZshShellIntegrationProviderTests|FullyQualifiedName~ZshBootstrapBuilderTests|FullyQualifiedName~ShellIntegrationRegistryTests"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/ShellIntegration/Zsh src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ZshShellIntegrationProviderTests.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ZshBootstrapBuilderTests.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationRegistryTests.cs
git commit -m "Add Zsh shell integration provider for Command Assist"
```

### Task 6: Validate Zsh lifecycle compatibility

**Files:**
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`
- Modify: `tests/NovaTerminal.Tests/OscShellIntegrationTests.cs`

**Step 1: Write the failing tests**

Add tests that assert:
- Zsh structured accepted-command payloads are treated identically to Bash and PowerShell
- lifecycle ordering remains `A -> C -> D`
- controller history persistence remains shell-agnostic

Example assertion:

```csharp
[Fact]
public async Task HandleShellIntegrationEventAsync_ForZshAcceptedCommand_StoresShellKindSpecificEntry()
{
    var historyStore = new InMemoryHistoryStore();
    var controller = CreateController(historyStore: historyStore);
    controller.SetShellIntegrationEnabled(true);
    controller.UpdateSessionContext("zsh", "/repo", "profile-1", "session-1", null, false, true);

    await controller.HandleShellIntegrationEventAsync(new ShellIntegrationEvent(
        ShellIntegrationEventType.CommandAccepted,
        DateTimeOffset.UtcNow,
        "git status",
        "/repo",
        null,
        null));

    Assert.Contains(historyStore.Entries, e => e.ShellKind == "zsh");
}
```

**Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistControllerTests|FullyQualifiedName~OscShellIntegrationTests"
```

Expected: FAIL if Zsh scenarios reveal shell-kind or controller assumptions.

**Step 3: Write minimal implementation**

Fix only the generic contract assumptions exposed by the tests. Do not add Zsh-specific branches to the controller unless there is no narrower alternative.

**Step 4: Run tests to verify they pass**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistControllerTests|FullyQualifiedName~OscShellIntegrationTests"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs tests/NovaTerminal.Tests/OscShellIntegrationTests.cs
git commit -m "Harden Command Assist lifecycle handling for Zsh provider"
```

### Task 7: Add failing Fish provider contract tests

**Files:**
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Fish/FishShellIntegrationProvider.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ShellIntegration/Fish/FishBootstrapBuilder.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/FishShellIntegrationProviderTests.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/FishBootstrapBuilderTests.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationRegistryTests.cs`

**Step 1: Write the failing tests**

Add tests that assert:
- the registry returns a Fish provider for shell kind `fish`
- the bootstrap script emits the same normalized marker contract
- Fish integration can be disabled cleanly when launch args are incompatible

Example assertions:

```csharp
[Fact]
public void GetProvider_ForFishProfile_ReturnsFishProvider()
{
    var registry = CommandAssistInfrastructure.GetShellIntegrationRegistry();
    IShellIntegrationProvider? provider = registry.GetProvider("fish", new TerminalProfile { Command = "fish" });
    Assert.IsType<FishShellIntegrationProvider>(provider);
}

[Fact]
public void BuildScript_ContainsStructuredLifecycleMarkers()
{
    string script = FishBootstrapBuilder.BuildScript();

    Assert.Contains("]7;", script);
    Assert.Contains("]133;A", script);
    Assert.Contains("]133;C;", script);
    Assert.Contains("]133;D;", script);
}
```

**Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~FishShellIntegrationProviderTests|FullyQualifiedName~FishBootstrapBuilderTests|FullyQualifiedName~ShellIntegrationRegistryTests"
```

Expected: FAIL with missing Fish provider types.

**Step 3: Write minimal implementation**

Create:
- `FishShellIntegrationProvider`
- `FishBootstrapBuilder`

Keep this task narrow:
- same normalized marker contract as the other providers
- no remote integration
- no shell-specific UI changes

**Step 4: Run tests to verify they pass**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~FishShellIntegrationProviderTests|FullyQualifiedName~FishBootstrapBuilderTests|FullyQualifiedName~ShellIntegrationRegistryTests"
```

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/ShellIntegration/Fish src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/FishShellIntegrationProviderTests.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/FishBootstrapBuilderTests.cs tests/NovaTerminal.Tests/CommandAssist/ShellIntegration/ShellIntegrationRegistryTests.cs
git commit -m "Add Fish shell integration provider for Command Assist"
```

### Task 8: Run regression coverage and update shell-integration docs

**Files:**
- Modify: `docs/command-assist/CommandAssist_PowerShell_Integration.md`
- Modify: `docs/command-assist/CommandAssist_ShellIntegration_Gaps.md`
- Modify: `docs/command-assist/CommandAssist.md`

**Step 1: Write the doc changes**

Update the docs so they reflect the real post-implementation state:
- PowerShell is fully structured if Task 1 is complete
- Bash, Zsh, and Fish provider support exists for local shell startup
- SSH launch-plan injection is still out of scope unless explicitly added later
- heuristic fallback remains active for unsupported shells and failed integration sessions

**Step 2: Run focused regression tests**

Run:

```powershell
dotnet test tests\NovaTerminal.Tests\NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~PowerShell|FullyQualifiedName~CommandAssist|FullyQualifiedName~OscShellIntegrationTests|FullyQualifiedName~TerminalPaneCommandAssistShortcutTests|FullyQualifiedName~TerminalViewKeyHandlingTests"
```

Expected: PASS.

**Step 3: Run broader app regression tests**

Run:

```powershell
dotnet test NovaTerminal.sln -c Release
```

Expected: PASS, except for any pre-existing unrelated failures already known in the repo.

**Step 4: Verify docs match code**

Manually verify:
- `CommandAssist.md` no longer implies shell gaps that were closed
- `CommandAssist_PowerShell_Integration.md` no longer says command text capture is heuristic if Task 1 completed
- `CommandAssist_ShellIntegration_Gaps.md` correctly lists only the remaining gaps

**Step 5: Commit**

```bash
git add docs/command-assist/CommandAssist_PowerShell_Integration.md docs/command-assist/CommandAssist_ShellIntegration_Gaps.md docs/command-assist/CommandAssist.md
git commit -m "Document expanded Command Assist shell integration support"
```

### Notes For Execution

- Keep shell integration App-layer only. Do not move shell-specific logic into VT parsing or rendering.
- Use the existing parser surface first: `OSC 7`, `OSC 133;A`, `OSC 133;B`, `OSC 133;C`, `OSC 133;D`.
- Do not special-case provider behavior inside `CommandAssistController` unless a test proves the generic contract is insufficient.
- Keep SSH out of scope for provider injection in this plan. `TerminalPane` currently skips shell-integration launch-plan injection for SSH profiles.
- Preserve heuristic fallback for any unsupported or partially integrated session.

### Recommended Execution Order

1. Task 1
2. Task 2
3. Task 3
4. Task 4
5. Task 5
6. Task 6
7. Task 7
8. Task 8
