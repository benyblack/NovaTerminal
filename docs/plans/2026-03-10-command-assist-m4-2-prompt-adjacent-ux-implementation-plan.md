# Command Assist M4.2 Prompt-Adjacent UX Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Refactor Command Assist from a bottom-docked footer panel into a prompt-adjacent floating bubble and popup while preserving the existing controller, helper, and shell-integration behavior.

**Architecture:** Keep the current `CommandAssistController` and provider stack intact, and replace only the presentation/placement layer in `TerminalPane` and Avalonia views. Add a pane-local anchor calculator that computes safe bubble and popup rectangles from terminal metrics and a prompt-region estimate.

**Tech Stack:** C#, .NET 10, Avalonia, xUnit, existing `NovaTerminal.App.CommandAssist` subsystem, pane-local overlay composition in `TerminalPane`.

---

### Task 1: Lock prompt-adjacent placement behavior with tests

**Files:**
- Create: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistAnchorCalculatorTests.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistAnchorCalculator.cs`

**Step 1: Write the failing tests**

Add tests for:
- bubble placement above prompt region by default
- popup flips when insufficient room above
- placement clamps inside pane bounds
- fallback anchor for uncertain prompt placement

```csharp
[Fact]
public void Calculate_WhenSpaceExistsAbovePrompt_PlacesBubbleAbovePrompt()
{
    var calculator = new CommandAssistAnchorCalculator();

    CommandAssistAnchorLayout layout = calculator.Calculate(new CommandAssistAnchorRequest(
        PaneWidth: 1000,
        PaneHeight: 700,
        CellHeight: 18,
        CursorVisualRow: 34,
        VisibleRows: 36,
        BubbleWidth: 420,
        BubbleHeight: 36,
        PopupWidth: 520,
        PopupHeight: 220));

    Assert.True(layout.BubbleRect.Bottom < layout.PromptRect.Top);
}
```

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistAnchorCalculatorTests"
```

Expected: FAIL with missing calculator/layout types.

**Step 3: Write minimal implementation**

Implement:
- request/result records
- deterministic placement logic
- safe clamping rules

Avoid any controller dependencies.

**Step 4: Run test to verify it passes**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistAnchorCalculatorTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/CommandAssist/CommandAssistAnchorCalculatorTests.cs src/NovaTerminal.App/CommandAssist/Application/CommandAssistAnchorCalculator.cs
git commit -m "Add Command Assist prompt anchor calculator"
```

### Task 2: Split the footer view model into bubble and popup state

**Files:**
- Create: `src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBubbleViewModel.cs`
- Create: `src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistPopupViewModel.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBarViewModel.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- compact bubble text/state binding
- popup-only visibility when expanded content is needed
- hidden popup when suggest mode is collapsed

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistLayoutTests"
```

Expected: FAIL because the current single footer viewmodel cannot express bubble/popup split.

**Step 3: Write minimal implementation**

Refactor viewmodel state so:
- the existing controller can still set mode/results
- presentation state can be exposed as bubble state plus popup state
- no ranking/help logic moves into the viewmodel

**Step 4: Run test to verify it passes**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistLayoutTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBubbleViewModel.cs src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistPopupViewModel.cs src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBarViewModel.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs
git commit -m "Split Command Assist footer state into bubble and popup"
```

### Task 3: Add floating bubble and popup views

**Files:**
- Create: `src/NovaTerminal.App/CommandAssist/Views/CommandAssistBubbleView.axaml`
- Create: `src/NovaTerminal.App/CommandAssist/Views/CommandAssistBubbleView.axaml.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Views/CommandAssistPopupView.axaml`
- Create: `src/NovaTerminal.App/CommandAssist/Views/CommandAssistPopupView.axaml.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- bubble view exists and binds collapsed state
- popup view exists and binds result list/detail state
- popup remains separate from terminal layout rows

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistLayoutTests"
```

Expected: FAIL because views do not exist.

**Step 3: Write minimal implementation**

Build:
- compact bubble view
- floating popup view
- no bottom dock row assumptions

Keep styling intentionally light and compact.

**Step 4: Run test to verify it passes**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistLayoutTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Views/CommandAssistBubbleView.axaml src/NovaTerminal.App/CommandAssist/Views/CommandAssistBubbleView.axaml.cs src/NovaTerminal.App/CommandAssist/Views/CommandAssistPopupView.axaml src/NovaTerminal.App/CommandAssist/Views/CommandAssistPopupView.axaml.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs
git commit -m "Add Command Assist floating bubble and popup views"
```

### Task 4: Replace footer hosting with overlay composition in TerminalPane

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- no dedicated bottom assist row remains in `TerminalPane`
- assist views are hosted as overlays in the terminal layer
- showing the assist does not change pane layout height allocation

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistLayoutTests"
```

Expected: FAIL because the footer-based host is still present.

**Step 3: Write minimal implementation**

Modify `TerminalPane` to host:
- bubble overlay
- popup overlay

Do not use an `Auto` row for assist content.

**Step 4: Run test to verify it passes**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistLayoutTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs
git commit -m "Host Command Assist as prompt-adjacent overlays"
```

### Task 5: Feed terminal metrics and prompt hints into placement logic

**Files:**
- Modify: `src/NovaTerminal.App/Core/TerminalView.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/TerminalViewKeyHandlingTests.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistAnchorCalculatorTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- pane can derive a prompt-region estimate from visible cursor row and cell metrics
- anchor updates when metrics or visible cursor placement change
- no dependence on terminal buffer content mutation

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistAnchorCalculatorTests|TerminalViewKeyHandlingTests"
```

Expected: FAIL because prompt-region feed is not wired into placement yet.

**Step 3: Write minimal implementation**

Expose only the minimal pane-side information needed for anchor calculation:
- visible cursor row hint
- current cell metrics
- pane bounds

Keep this App-side and non-invasive.

**Step 4: Run test to verify it passes**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistAnchorCalculatorTests|TerminalViewKeyHandlingTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/TerminalView.cs src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.Tests/CommandAssist/TerminalViewKeyHandlingTests.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistAnchorCalculatorTests.cs
git commit -m "Wire prompt-region hints into Command Assist placement"
```

### Task 6: Preserve current Command Assist behavior with collapsed/expanded states

**Files:**
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- suggest mode stays collapsed by default while typing
- popup opens when browsing or explicit help/search/fix requires richer content
- help/search/fix still populate the expanded content correctly

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests"
```

Expected: FAIL because collapsed/expanded presentation state is not expressed yet.

**Step 3: Write minimal implementation**

Add presentation-state flags only:
- collapsed bubble visible
- popup visible
- popup auto-open policy per mode

Do not rework ranking/help/fix logic.

**Step 4: Run test to verify it passes**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistControllerTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs
git commit -m "Add collapsed and expanded Command Assist presentation states"
```

### Task 7: Add constrained-pane fallback behavior

**Files:**
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistAnchorCalculator.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistAnchorCalculatorTests.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- narrow pane reduces bubble density
- short pane keeps bubble but constrains popup
- unreliable anchor falls back to stable lower safe zone

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistAnchorCalculatorTests|CommandAssistLayoutTests"
```

Expected: FAIL because degraded placement/layout rules are not implemented.

**Step 3: Write minimal implementation**

Implement:
- narrow-pane clamping
- short-pane popup bounds
- stable fallback anchor placement

Keep rules explicit and deterministic.

**Step 4: Run test to verify it passes**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistAnchorCalculatorTests|CommandAssistLayoutTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Application/CommandAssistAnchorCalculator.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistAnchorCalculatorTests.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs
git commit -m "Add constrained-pane fallbacks for Command Assist overlays"
```

### Task 8: Preserve key ownership and alt-screen behavior

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistKeyRouterTests.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/TerminalPaneCommandAssistShortcutTests.cs`
- Modify: `tests/NovaTerminal.Tests/CommandAssist/AlternateScreenTests.cs`

**Step 1: Write the failing tests**

Add tests for:
- assist-owned keys still route correctly with bubble/popup split
- alt-screen hides both bubble and popup immediately
- popup visibility does not change shell key leakage behavior

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistKeyRouterTests|TerminalPaneCommandAssistShortcutTests|AlternateScreenTests"
```

Expected: FAIL if the refactor broke routing or hide rules.

**Step 3: Write minimal implementation**

Adjust pane routing only as needed to:
- treat collapsed and expanded states correctly
- keep shell leakage blocked when the assist owns the interaction
- preserve the existing alt-screen hard hide

**Step 4: Run test to verify it passes**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "CommandAssistKeyRouterTests|TerminalPaneCommandAssistShortcutTests|AlternateScreenTests"
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml.cs tests/NovaTerminal.Tests/CommandAssist/CommandAssistKeyRouterTests.cs tests/NovaTerminal.Tests/CommandAssist/TerminalPaneCommandAssistShortcutTests.cs tests/NovaTerminal.Tests/CommandAssist/AlternateScreenTests.cs
git commit -m "Preserve Command Assist routing and alt-screen behavior"
```

### Task 9: Run targeted regressions and compile

**Files:**
- Modify: none unless regressions are found

**Step 1: Run focused tests**

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

**Step 3: Fix any regressions**

If anything fails, fix the smallest issue and rerun the same commands until clean.

**Step 4: Commit stabilization**

```bash
git add -A
git commit -m "Finalize prompt-adjacent Command Assist UX"
```

### Task 10: Prepare PR summary

**Files:**
- Modify: none required

**Step 1: Gather the final diff**

Run:

```bash
git diff --stat main...HEAD
git log --oneline main..HEAD
```

**Step 2: Write the PR notes**

Include:
- footer removed as primary interaction model
- prompt-adjacent bubble + popup added
- no VT/render changes
- no terminal layout resizing
- alt-screen behavior preserved

**Step 3: Push and open PR**

Run:

```bash
git push -u origin <branch-name>
```

Then open the PR with the summary above.
