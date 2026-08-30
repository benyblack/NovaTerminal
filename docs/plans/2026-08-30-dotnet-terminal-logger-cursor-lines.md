# .NET Terminal Logger Cursor-Line Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make .NET 10 terminal-logger progress redraws update in place by supporting the standard CSI cursor-line commands.

**Architecture:** Add CSI `E` and `F` routing beside the existing relative cursor commands in `AnsiParser`. Keep movement semantics consistent with CSI `A` and `B`, including scroll-region clamping, while resetting the horizontal cursor position as required by CNL/CPL.

**Tech Stack:** C# 14, .NET 10, xUnit v3

---

### Task 1: Reproduce the missing CNL/CPL behavior

**Files:**
- Create: `tests/NovaTerminal.VT.Tests/CursorLinePositioningTests.cs`

**Step 1: Write the failing cursor-command tests**

Add theories for omitted, zero, explicit, and oversized CSI `E`/`F` parameters. Start from a nonzero column and assert the expected clamped row and column zero. Add cases with a restricted scrolling region and cases starting outside that region.

**Step 2: Write the failing .NET refresh regression test**

Process an initial progress row followed by repeated `\x1b[1F\r\n\x1b[K...\r\n` refreshes. Assert that the refreshed text occupies the same row and that no timer staircase appears below it.

**Step 3: Run the focused tests to verify RED**

Run:

```powershell
dotnet test tests/NovaTerminal.VT.Tests/NovaTerminal.VT.Tests.csproj -nologo -nodeReuse:false --filter FullyQualifiedName~CursorLinePositioningTests
```

Expected: failures showing that CSI `E` and `F` leave the cursor row/column unchanged and that the refresh stream consumes extra rows.

### Task 2: Implement standard CSI E/F handling

**Files:**
- Modify: `src/NovaTerminal.VT/AnsiParser.cs`

**Step 1: Implement CSI E**

Add a `case 'E'` next-line command beside CSI `B`. Use `Math.Max(1, arg0)`, apply the same scroll-region/viewport clamping as CSI `B`, set `CursorCol` to zero, and invalidate the buffer.

**Step 2: Implement CSI F**

Add a `case 'F'` preceding-line command beside CSI `A`. Use `Math.Max(1, arg0)`, apply the same scroll-region/viewport clamping as CSI `A`, set `CursorCol` to zero, and invalidate the buffer.

**Step 3: Run the focused tests to verify GREEN**

Run the filtered command from Task 1.

Expected: all `CursorLinePositioningTests` pass.

### Task 3: Verify and commit the fix

**Files:**
- Test: `tests/NovaTerminal.VT.Tests/CursorLinePositioningTests.cs`
- Modify: `src/NovaTerminal.VT/AnsiParser.cs`

**Step 1: Run the complete VT test project**

```powershell
dotnet test tests/NovaTerminal.VT.Tests/NovaTerminal.VT.Tests.csproj -nologo --no-restore -nodeReuse:false
```

Expected: zero failed tests.

**Step 2: Build the VT project**

```powershell
dotnet build src/NovaTerminal.VT/NovaTerminal.VT.csproj -nologo --no-restore -nodeReuse:false
```

Expected: exit code zero; any analyzer warnings match the clean baseline.

**Step 3: Review the diff and commit**

Review `git diff --check` and the complete branch diff, then commit the parser and regression tests with a focused fix message.

### Task 4: Publish and respond to review

**Step 1: Push and open a pull request**

Push `codex/dotnet-terminal-logger-cursor-lines` and create a PR describing the captured .NET 10 sequence, parser fix, and verification.

**Step 2: Monitor checks and Greptile review**

Wait for automated checks and Greptile feedback. Read all feedback, verify each finding against the VT semantics and tests, and implement only technically valid changes.

**Step 3: Re-verify and push review fixes**

For each accepted finding, add or adjust a failing test first when behavior changes, implement the minimal fix, run focused and complete VT tests, commit, and push.
