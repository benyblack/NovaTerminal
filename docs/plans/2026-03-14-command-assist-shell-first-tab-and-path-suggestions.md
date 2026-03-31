# Command Assist Shell-First Tab And Path Suggestions Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `Tab` shell-owned for completion while adding Command Assist path suggestions that are accepted with `Ctrl+Enter`.

**Architecture:** Keep terminal input behavior shell-first by changing Command Assist key arbitration so `Tab` is never consumed. Add a filesystem-backed path suggestion provider inside the Command Assist suggestion pipeline, scoped to local sessions only, and keep insertion additive through existing insertion-planner behavior.

**Tech Stack:** C#/.NET 10, Avalonia key handling, existing Command Assist domain/application layers, xUnit/Avalonia.Headless tests.

---

### Task 1: Lock Key Arbitration To Shell-First Tab

**Files:**
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistKeyRouter.cs`
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBubbleViewModel.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistKeyRouterTests.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/TerminalPaneCommandAssistShortcutTests.cs`

**Step 1: Write failing tests for new key ownership contract**

Add tests that assert:
- `Tab` is not assist-owned even when assist is visible.
- `Ctrl+Enter` is assist-owned when assist is visible.
- `Ctrl+Enter` is not assist-owned when assist is hidden.

**Step 2: Run tests to verify failure**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistKeyRouterTests"`

Expected: FAIL on new assertions until router logic is updated.

**Step 3: Implement minimal key routing changes**

Update `CommandAssistKeyRouter.IsAssistOwnedKey`:
- Remove `Key.Tab` from assist-owned keys.
- Add `Ctrl+Enter` as assist-owned key.
- Keep existing `Escape`, `Up`, `Down`, `Ctrl+Shift+P`.

Update `TerminalPane.TryHandleCommandAssistKey`:
- Remove `Tab` acceptance path.
- Add `Ctrl+Enter` path that calls `TryInsertSelectedCommandAssistSuggestion()`.

Update bubble hint text to reflect the new default keys (`Ctrl+Enter` accept, `Ctrl+Space` toggle).

**Step 4: Run tests to verify pass**

Run:
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistKeyRouterTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~TerminalPaneCommandAssistShortcutTests"`

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Application/CommandAssistKeyRouter.cs \
        src/NovaTerminal.App/Controls/TerminalPane.axaml.cs \
        src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBubbleViewModel.cs \
        tests/NovaTerminal.Tests/CommandAssist/CommandAssistKeyRouterTests.cs \
        tests/NovaTerminal.Tests/CommandAssist/TerminalPaneCommandAssistShortcutTests.cs
git commit -m "Make Tab shell-owned and move assist accept to Ctrl+Enter"
```

### Task 2: Add Local Path Suggestion Provider For Command Assist

**Files:**
- Create: `src/NovaTerminal.App/CommandAssist/Domain/IPathSuggestionProvider.cs`
- Create: `src/NovaTerminal.App/CommandAssist/Domain/FileSystemPathSuggestionProvider.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Domain/CommandAssistSuggestionEngine.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Models/AssistSuggestionType.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Models/CommandAssistQueryContext.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistSuggestionEngineTests.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/FileSystemPathSuggestionProviderTests.cs`

**Step 1: Write failing tests for path suggestion behavior**

Add deterministic tests for:
- `cd ~/h` style prefix emits path suggestions.
- Directory suggestions are listed before file suggestions.
- Suggestions are additive continuations of the current input.
- Local path suggestions are disabled when context is remote.

Use temp directories/files in tests to avoid machine-specific dependencies.

**Step 2: Run tests to verify failure**

Run:
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~FileSystemPathSuggestionProviderTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistSuggestionEngineTests"`

Expected: FAIL until provider + integration are implemented.

**Step 3: Implement minimal provider and integrate**

Add provider that:
- Detects path context from current input token and common path commands (`cd`, `ls`, `cat`, `mv`, `cp`, `rm`).
- Resolves candidates from current working directory.
- Supports `~/` expansion.
- Emits `AssistSuggestion` rows with `Type = AssistSuggestionType.Path`.
- Returns insert text that strictly extends current query (for insertion planner compatibility).
- Limits count and uses stable ordering (directories first, then name).

Integrate provider into `CommandAssistSuggestionEngine` and `CommandAssistInfrastructure`.
Pass remote-session flag by extending `CommandAssistQueryContext` and wiring `_isRemote` from `CommandAssistController`.

**Step 4: Run tests to verify pass**

Run:
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~FileSystemPathSuggestionProviderTests"`
- `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistSuggestionEngineTests"`

Expected: PASS.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/CommandAssist/Domain/IPathSuggestionProvider.cs \
        src/NovaTerminal.App/CommandAssist/Domain/FileSystemPathSuggestionProvider.cs \
        src/NovaTerminal.App/CommandAssist/Domain/CommandAssistSuggestionEngine.cs \
        src/NovaTerminal.App/CommandAssist/Models/AssistSuggestionType.cs \
        src/NovaTerminal.App/CommandAssist/Models/CommandAssistQueryContext.cs \
        src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs \
        src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs \
        tests/NovaTerminal.Tests/CommandAssist/FileSystemPathSuggestionProviderTests.cs \
        tests/NovaTerminal.Tests/CommandAssist/CommandAssistSuggestionEngineTests.cs
git commit -m "Add local path suggestions to command assist without Tab hijacking"
```

### Task 3: Verify End-To-End Behavior And AOT Safety

**Files:**
- Verify only: `src/NovaTerminal.App/CommandAssist/**`, `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`

**Step 1: Run focused command-assist suite**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssist"`

Expected: PASS.

**Step 2: Run PTY smoke lane**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "Category=PtySmoke"`

Expected: PASS.

**Step 3: Run AOT publish verification**

Run: `dotnet publish src/NovaTerminal.App/NovaTerminal.App.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o artifacts/publish/win-x64`

Expected: publish succeeds and no new Command Assist trim regressions.

**Step 4: Manual smoke checklist**

Verify:
- `cd ~/h` + `Tab` uses shell completion (no assist insertion).
- `Ctrl+Space` opens/refreshes assist.
- Path suggestion rows appear for path-like input.
- `Ctrl+Enter` accepts selected path suggestion.
- SSH pane does not surface local filesystem path suggestions.

**Step 5: Commit verification notes (optional)**

```bash
git commit --allow-empty -m "chore: verify command assist shell-first tab and path suggestion behavior"
```
