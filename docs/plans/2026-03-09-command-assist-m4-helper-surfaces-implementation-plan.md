# Command Assist M4 Helper Surfaces Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add pane-local Command Assist helper surfaces for Help, Recipes/Examples, Fix, and Explain Selection without touching VT/render core or rendering anything into the terminal grid.

**Architecture:** Extend the existing `NovaTerminal.App.CommandAssist` subsystem rather than creating a second helper framework. Keep `TerminalPane` as the pane-local integration seam, keep `CommandAssistController` as the orchestration point, add deterministic local provider interfaces for docs/recipes/fix insights, and reuse the existing assist overlay with mode-aware content.

**Tech Stack:** C#, .NET 10, Avalonia, xUnit, existing `NovaTerminal.App.CommandAssist` subsystem, deterministic local provider services.

---

### Task 1: Add the M4 model and contract tests

**Files:**
- Create: `tests/NovaTerminal.Tests/CommandAssist/LocalCommandDocsProviderTests.cs`
- Create: `tests/NovaTerminal.Tests/CommandAssist/SeedRecipeProviderTests.cs`
- Create: `tests/NovaTerminal.Tests/CommandAssist/HeuristicErrorInsightServiceTests.cs`
- Create: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistModeRouterTests.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Models/CommandAssistMode.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Models/CommandHelpQuery.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Models/CommandFailureContext.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Models/CommandHelpItem.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Models/CommandFixSuggestion.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Models/CommandAssistContextSnapshot.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Domain/ICommandDocsProvider.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Domain/IRecipeProvider.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Domain/IErrorInsightService.cs`

**Step 1: Write the failing tests**

Add tests that lock the expected M4 contracts:

```csharp
[Fact]
public async Task GetHelpAsync_WhenCommandIsRecognized_ReturnsLocalHelp()
{
    var provider = new LocalCommandDocsProvider();
    var query = new CommandHelpQuery("git checkout", "git", "bash", "/repo", null, null);

    IReadOnlyList<CommandHelpItem> result = await provider.GetHelpAsync(query, CancellationToken.None);

    Assert.Contains(result, item => item.Title.Contains("git", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public async Task AnalyzeAsync_WhenCommandNotFound_ReturnsHighConfidenceFix()
{
    var service = new HeuristicErrorInsightService();
    var context = new CommandFailureContext("gti status", 127, "bash", "/repo", "command not found: gti", false, null);

    IReadOnlyList<CommandFixSuggestion> result = await service.AnalyzeAsync(context, CancellationToken.None);

    Assert.Contains(result, item => item.Confidence >= 0.8);
}
```

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "LocalCommandDocsProviderTests|SeedRecipeProviderTests|HeuristicErrorInsightServiceTests|CommandAssistModeRouterTests"
```

Expected: FAIL with missing types and interfaces.

**Step 3: Write the minimal model and contract code**

Add the new model records and service interfaces with no speculative fields beyond M4:
- `CommandAssistMode` = `Suggest`, `Search`, `Help`, `Fix`
- `CommandHelpQuery`
- `CommandFailureContext`
- `CommandHelpItem`
- `CommandFixSuggestion`
- `CommandAssistContextSnapshot`
- `ICommandDocsProvider`
- `IRecipeProvider`
- `IErrorInsightService`

**Step 4: Run tests again**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "LocalCommandDocsProviderTests|SeedRecipeProviderTests|HeuristicErrorInsightServiceTests|CommandAssistModeRouterTests"
```

Expected: still FAIL, but now on missing provider implementations rather than missing types.

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist/LocalCommandDocsProviderTests.cs tests/NovaTerminal.Tests/CommandAssist/SeedRecipeProviderTests.cs tests/NovaTerminal.Tests/CommandAssist/HeuristicErrorInsightServiceTests.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistModeRouterTests.cs src/NovaTerminal.App/CommandAssist/Models/CommandAssistMode.cs src/NovaTerminal.App/CommandAssist/Models/CommandHelpQuery.cs src/NovaTerminal.App/CommandAssist/Models/CommandFailureContext.cs src/NovaTerminal.App/CommandAssist/Models/CommandHelpItem.cs src/NovaTerminal.App/CommandAssist/Models/CommandFixSuggestion.cs src/NovaTerminal.App/CommandAssist/Models/CommandAssistContextSnapshot.cs src/NovaTerminal.App/CommandAssist/Domain/ICommandDocsProvider.cs src/NovaTerminal.App/CommandAssist/Domain/IRecipeProvider.cs src/NovaTerminal.App/CommandAssist/Domain/IErrorInsightService.cs
git commit -m "Add Command Assist M4 helper contracts"
```

### Task 2: Implement deterministic local docs and recipe providers

**Files:**
- Create: `src/NovaTerminal.App/CommandAssist/Domain/LocalCommandDocsProvider.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Domain/SeedRecipeProvider.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/LocalCommandDocsProviderTests.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/SeedRecipeProviderTests.cs`

**Step 1: Write the failing tests**

Add focused tests for:
- recognized command returns at least one local help item
- unknown command returns empty list
- recipe lookup filters by command token
- shell-specific examples prefer matching shell kind when present

```csharp
[Fact]
public async Task GetRecipesAsync_WhenShellMatches_PrefersShellSpecificRecipes()
{
    var provider = new SeedRecipeProvider();
    var query = new CommandHelpQuery("Get-ChildItem", "Get-ChildItem", "pwsh", "C:/repo", null, null);

    IReadOnlyList<CommandHelpItem> result = await provider.GetRecipesAsync(query, CancellationToken.None);

    Assert.Equal("pwsh", result[0].ShellKind);
}
```

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "LocalCommandDocsProviderTests|SeedRecipeProviderTests"
```

Expected: FAIL because providers are not implemented.

**Step 3: Write minimal implementation**

Implement:
- `LocalCommandDocsProvider` with a small in-memory seed for high-value commands (`git`, `docker`, `ls`, `cd`, `grep`, `Get-ChildItem`, `Set-Location`)
- `SeedRecipeProvider` with a small in-memory seed of examples/recipes
- deterministic ordering only
- no file-backed content yet

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "LocalCommandDocsProviderTests|SeedRecipeProviderTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Domain/LocalCommandDocsProvider.cs src/NovaTerminal.App/CommandAssist/Domain/SeedRecipeProvider.cs tests/NovaTerminal.Tests/CommandAssist/LocalCommandDocsProviderTests.cs tests/NovaTerminal.Tests/CommandAssist/SeedRecipeProviderTests.cs
git commit -m "Add local Command Assist docs and recipe providers"
```

### Task 3: Implement heuristic fix insight service

**Files:**
- Create: `src/NovaTerminal.App/CommandAssist/Domain/HeuristicErrorInsightService.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/HeuristicErrorInsightServiceTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- `command not found`
- `is not recognized as an internal or external command`
- `No such file or directory`
- typo correction from known commands
- low-confidence failures do not auto-open-worthy results

```csharp
[Fact]
public async Task AnalyzeAsync_WhenPowerShellCommandNotRecognized_SuggestsLikelyCommand()
{
    var service = new HeuristicErrorInsightService();
    var context = new CommandFailureContext("Get-ChldItem", 1, "pwsh", "C:/repo", "The term 'Get-ChldItem' is not recognized", false, null);

    IReadOnlyList<CommandFixSuggestion> result = await service.AnalyzeAsync(context, CancellationToken.None);

    Assert.Contains(result, item => item.SuggestedCommand.Contains("Get-ChildItem"));
}
```

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "HeuristicErrorInsightServiceTests"
```

Expected: FAIL because the service is not implemented.

**Step 3: Write minimal implementation**

Implement conservative heuristics only:
- known error pattern detection
- low edit-distance correction against seeded doc/recipe command names
- shell mismatch hints
- path invocation hints such as `./foo`
- confidence scoring

Do not infer arbitrary stderr.

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "HeuristicErrorInsightServiceTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Domain/HeuristicErrorInsightService.cs tests/NovaTerminal.Tests/CommandAssist/HeuristicErrorInsightServiceTests.cs
git commit -m "Add heuristic Command Assist fix insights"
```

### Task 4: Extend assist row models for helper content

**Files:**
- Modify: `src/NovaTerminal.App/CommandAssist/Models/AssistSuggestion.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Models/AssistSuggestionType.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistSuggestionEngineTests.cs`

**Step 1: Write the failing tests**

Add tests that assert helper rows can carry:
- `Description`
- `Recipe`, `Doc`, `Fix` types
- badges that can coexist with existing history/snippet rows

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistSuggestionEngineTests"
```

Expected: FAIL because the current row contract is too narrow.

**Step 3: Write minimal implementation**

Modify:
- `AssistSuggestionType` to add `Recipe`, `Doc`, `Fix`
- `AssistSuggestion` to add `Description`

Update any existing constructors/usages to compile with the new field.

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistSuggestionEngineTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Models/AssistSuggestion.cs src/NovaTerminal.App/CommandAssist/Models/AssistSuggestionType.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistSuggestionEngineTests.cs
git commit -m "Extend Command Assist row model for helper modes"
```

### Task 5: Implement mode routing and recognized-command parsing

**Files:**
- Create: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistModeRouter.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Application/RecognizedCommandParser.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistModeRouterTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- explicit help requests choose `Help`
- high-confidence failures choose `Fix`
- low-confidence failures remain in `Suggest`
- parser extracts the leading executable/command token from simple commands

```csharp
[Fact]
public void ChooseMode_WhenFailureHasLowConfidence_RemainsSuggest()
{
    var router = new CommandAssistModeRouter();

    CommandAssistMode mode = router.ChooseModeForFailure(0.2);

    Assert.Equal(CommandAssistMode.Suggest, mode);
}
```

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistModeRouterTests"
```

Expected: FAIL because router/parser are not implemented.

**Step 3: Write minimal implementation**

Implement:
- a simple router with high-confidence gating
- a lightweight parser that extracts the primary command token without pretending to fully parse shell syntax

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistModeRouterTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Application/CommandAssistModeRouter.cs src/NovaTerminal.App/CommandAssist/Application/RecognizedCommandParser.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistModeRouterTests.cs
git commit -m "Add Command Assist M4 mode routing"
```

### Task 6: Implement helper result shaping

**Files:**
- Create: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistResultBuilder.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`

**Step 1: Write the failing tests**

Add tests that assert:
- docs become `AssistSuggestionType.Doc`
- recipes become `AssistSuggestionType.Recipe`
- fix suggestions become `AssistSuggestionType.Fix`
- helper rows carry description and badges into the view model

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests"
```

Expected: FAIL because helper rows are not being built.

**Step 3: Write minimal implementation**

Implement `CommandAssistResultBuilder` to convert:
- `CommandHelpItem`
- `CommandFixSuggestion`
- existing history/snippet items

into one common `AssistSuggestion` row shape.

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests"
```

Expected: PASS for the new result-shaping tests.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Application/CommandAssistResultBuilder.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs
git commit -m "Add Command Assist helper result shaping"
```

### Task 7: Extend controller with Help and Fix entry points

**Files:**
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBarViewModel.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- `OpenHelp()` from current query
- `HandleCommandFailureAsync()` opening `Fix` for high-confidence failures
- low-confidence failure not auto-opening `Fix`
- `ExplainSelectionAsync()` using explicit selected text
- helper modes staying hidden in alt-screen

```csharp
[Fact]
public async Task HandleCommandFailureAsync_WhenInsightIsHighConfidence_OpensFixMode()
{
    var controller = CreateController();

    await controller.HandleCommandFailureAsync(CreateFailureContext("gti status", 127, "command not found"));

    Assert.Equal("Fix", controller.ViewModel.ModeLabel);
    Assert.True(controller.ViewModel.IsVisible);
}
```

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests"
```

Expected: FAIL because controller entry points and mode state do not exist yet.

**Step 3: Write minimal implementation**

Extend `CommandAssistController` to:
- accept docs/recipe/error-insight providers
- support `Help` and `Fix` modes
- keep `Suggest`/`Search` unchanged
- build mode-aware rows using `CommandAssistResultBuilder`
- preserve alt-screen hiding

Keep all helper behavior best-effort and pane-local.

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBarViewModel.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs
git commit -m "Add Command Assist help and fix modes"
```

### Task 8: Register the M4 providers in infrastructure

**Files:**
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`

**Step 1: Write the failing test**

Add or extend a controller integration test that constructs the real infrastructure path and verifies the controller can resolve helper providers.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests"
```

Expected: FAIL because infrastructure does not register the new providers.

**Step 3: Write minimal implementation**

Update `CommandAssistInfrastructure` to expose singleton or cached instances for:
- `ICommandDocsProvider`
- `IRecipeProvider`
- `IErrorInsightService`

Wire them into `TerminalPane.InitializeCommandAssist()`.

**Step 4: Run test to verify it passes**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs src/NovaTerminal.App/Controls/TerminalPane.axaml.cs
git commit -m "Wire Command Assist M4 providers into pane infrastructure"
```

### Task 9: Trigger fix mode from pane command failures

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- pane-level non-zero exit calling controller failure handling
- zero exit not triggering fix mode
- fix mode using last command text and pane metadata

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests"
```

Expected: FAIL because pane failure context is not forwarded.

**Step 3: Write minimal implementation**

In `TerminalPane.axaml.cs`:
- retain the last relevant submitted or accepted command text
- on `CommandFinished` with non-zero exit, build `CommandFailureContext`
- call `HandleCommandFailureAsync(...)`
- do not trigger during alt-screen

Keep this additive and do not alter parser semantics.

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs
git commit -m "Trigger Command Assist fix mode from command failures"
```

### Task 10: Add Explain Selection pane action

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/TerminalPaneCommandAssistShortcutTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- explain action only available when text is selected
- explain action routes selected text into the controller
- no explain action when selection is empty

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "TerminalPaneCommandAssistShortcutTests"
```

Expected: FAIL because the action does not exist.

**Step 3: Write minimal implementation**

Modify the pane context menu to add:
- `Explain Selection`

In code-behind:
- check `TermView.HasSelection()`
- read `TermView.GetSelectedText()`
- call `ExplainSelectionAsync(...)` on the controller

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "TerminalPaneCommandAssistShortcutTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.Tests/CommandAssist/TerminalPaneCommandAssistShortcutTests.cs
git commit -m "Add Command Assist explain selection action"
```

### Task 11: Extend the assist viewmodel and Avalonia view for helper modes

**Files:**
- Modify: `src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBarViewModel.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Views/CommandAssistBarView.axaml`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- mode-aware labels (`Help`, `Fix`)
- description/detail rendering
- empty-state text for helper modes
- overlay still not changing terminal layout

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistLayoutTests"
```

Expected: FAIL because the viewmodel/view do not expose helper-specific fields.

**Step 3: Write minimal implementation**

Extend the viewmodel with:
- `ModeLabel`
- `EmptyStateText`
- selected item description/detail text

Update the Avalonia view to:
- render mode-aware description text
- show empty states
- keep the overlay layout and sizing model unchanged

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistLayoutTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBarViewModel.cs src/NovaTerminal.App/CommandAssist/Views/CommandAssistBarView.axaml tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs
git commit -m "Render Command Assist helper mode content"
```

### Task 12: Add explicit Help shortcut routing

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/TerminalPaneCommandAssistShortcutTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- new help shortcut opens help on the active pane
- shortcut is only marked handled when help actually opens
- command palette shortcut behavior remains unchanged

**Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "TerminalPaneCommandAssistShortcutTests"
```

Expected: FAIL because no help shortcut exists yet.

**Step 3: Write minimal implementation**

In `MainWindow.axaml.cs`:
- add one explicit help shortcut, for example `Ctrl+Shift+H`
- route it to the active pane only
- keep all help logic in the pane/controller

**Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "TerminalPaneCommandAssistShortcutTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.Tests/CommandAssist/TerminalPaneCommandAssistShortcutTests.cs
git commit -m "Add Command Assist help shortcut"
```

### Task 13: Run the targeted regression suite

**Files:**
- Modify: none unless regressions are found

**Step 1: Run the focused M4 suite**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssist|ShellIntegration|AlternateScreen|HeadlessUI" --logger "console;verbosity=minimal"
```

Expected: PASS

**Step 2: Compile the App project**

Run:

```bash
dotnet msbuild src/NovaTerminal.App/NovaTerminal.App.csproj /t:Compile /p:Configuration=Release /v:minimal
```

Expected: Build succeeded

**Step 3: Fix any failures**

If tests fail, fix the smallest issue and rerun the same commands until clean.

**Step 4: Commit final stabilization**

```bash
git add -A
git commit -m "Finalize Command Assist M4 helper surfaces"
```

### Task 14: Prepare PR summary

**Files:**
- Modify: none required

**Step 1: Gather the final diff summary**

Run:

```bash
git diff --stat main...HEAD
git log --oneline main..HEAD
```

**Step 2: Write the PR notes**

Include:
- help mode
- recipes/examples
- fix mode heuristics
- explain selection
- no AI dependency
- untouched VT/render/PTTY boundaries

**Step 3: Push and open PR**

Run:

```bash
git push -u origin <branch-name>
```

Then open the PR with the summary above.
