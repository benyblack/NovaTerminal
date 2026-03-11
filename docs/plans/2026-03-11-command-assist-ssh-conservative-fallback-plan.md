# Command Assist SSH Conservative Fallback Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Keep Command Assist visually near the input line in SSH heuristic sessions without trusting remote prompt anchors.

**Architecture:** Extend the app-layer anchor calculator with a second fallback mode for unreliable prompt anchors. `TerminalPane` will continue to classify SSH heuristic sessions as untrusted, but it will pass the visible cursor row through so the calculator can place the bubble in a conservative cursor-adjacent band when the pane looks settled.

**Tech Stack:** C#, Avalonia, xUnit, existing Command Assist layout tests.

---

### Task 1: Add failing calculator tests for the SSH cursor-band fallback

**Files:**
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistAnchorCalculatorTests.cs`

**Step 1: Write the failing test**

- Add one test that passes `HasReliablePromptAnchor: false` with a lower-half `CursorVisualRow` and asserts:
  - `UsesPromptAnchor == false`
  - `BubbleRect.Bottom` is clearly above the fallback prompt row
  - `BubbleRect.Bottom` is higher than the old lower safe-zone placement
- Add one test that passes `HasReliablePromptAnchor: false` with a top-of-pane `CursorVisualRow` and asserts the bubble remains in the lower safe zone.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistAnchorCalculatorTests"
```

Expected: FAIL because the current unreliable-anchor path always uses the static lower safe zone.

### Task 2: Implement the calculator fallback

**Files:**
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistAnchorCalculator.cs`

**Step 1: Write minimal implementation**

- Add a cursor-band fallback path for `HasReliablePromptAnchor == false`.
- Use the visible cursor row only when it is in a stable lower region of the pane.
- Keep the existing lower safe-zone fallback when the cursor is too high or the computed bubble would crowd the pane.
- Do not change popup direction logic except through the new bubble rect.

**Step 2: Run tests to verify they pass**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistAnchorCalculatorTests"
```

Expected: PASS.

### Task 3: Add failing pane-level SSH layout tests

**Files:**
- Modify: `tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs`

**Step 1: Write the failing test**

- Add one pane-level SSH test where the terminal cursor is near the bottom and assert the bubble is positioned near the input line rather than the lower safe-zone baseline.
- Keep the existing SSH top-region test and tighten it if needed so it still proves the safe-zone fallback remains conservative.

**Step 2: Run test to verify it fails**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistLayoutTests"
```

Expected: FAIL on the new settled-SSH case before `TerminalPane` feeds the calculator correctly.

### Task 4: Wire the pane into the new fallback

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`

**Step 1: Write minimal implementation**

- Keep `IsCommandAssistPromptAnchorReliable` conservative for SSH.
- Ensure the current visible cursor row and metrics are still sent to the calculator for unreliable anchors.
- Avoid changing controller behavior or popup open policy.

**Step 2: Run pane tests**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~CommandAssistLayoutTests|FullyQualifiedName~CommandAssistAnchorCalculatorTests"
```

Expected: PASS.

### Task 5: Verify Command Assist and full suite

**Files:**
- No code changes expected.

**Step 1: Run focused Command Assist tests**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter "FullyQualifiedName~NovaTerminal.Tests.CommandAssist"
```

Expected: PASS.

**Step 2: Run full suite**

Run:

```bash
dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release
```

Expected: PASS.
