# Bundled Cascadia Mono PL Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Bundle `Cascadia Mono PL`, register it at app startup, include the upstream license notice, and make it the first-run terminal default for new users without changing terminal core behavior.

**Architecture:** Keep the change inside `NovaTerminal.App` resource/bootstrap/settings code. Ship the font and license under app assets, register the bundled family during Avalonia startup, and update the persisted default font family for fresh settings only.

**Tech Stack:** .NET 10, Avalonia 11, existing `NovaTerminal.App` startup path, xUnit, app asset packaging

---

### Task 1: Lock in the new default with tests

**Files:**
- Modify: `tests/NovaTerminal.Tests/Core/TerminalSettingsTests.cs`
- Modify: `src/NovaTerminal.App/Core/TerminalSettings.cs`

**Step 1: Write the failing test**

Add a test that asserts a fresh `TerminalSettings` instance defaults `FontFamily` to `Cascadia Mono PL`.

Add a second test that writes a settings JSON payload with an explicit `FontFamily`, loads it through `TerminalSettings.Load()`, and asserts the saved value is preserved.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~TerminalSettingsTests`

Expected: FAIL because the default is still `Consolas` or the preservation scenario is not covered.

**Step 3: Write minimal implementation**

Update `TerminalSettings.FontFamily` to `Cascadia Mono PL` and keep load behavior unchanged so saved values still win.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~TerminalSettingsTests`

Expected: PASS

**Step 5: Commit**

```bash
git add tests/NovaTerminal.Tests/Core/TerminalSettingsTests.cs src/NovaTerminal.App/Core/TerminalSettings.cs
git commit -m "feat: change first-run terminal font default"
```

### Task 2: Bundle the font and license notice

**Files:**
- Create: `src/NovaTerminal.App/Assets/Fonts/CascadiaMonoPL-Regular.otf`
- Create: `src/NovaTerminal.App/Assets/Fonts/LICENSES/CascadiaMono-OFL.txt`
- Modify: `src/NovaTerminal.App/NovaTerminal.App.csproj`

**Step 1: Write the failing test**

If a deterministic asset test seam is cheap, add a test that verifies the expected asset URIs or copied output paths are present. If not, use build verification as the check for this task.

**Step 2: Run verification to prove the gap**

Run: `dotnet build src/NovaTerminal.App/NovaTerminal.App.csproj -c Release`

Expected: output does not yet contain the bundled font asset or license notice.

**Step 3: Write minimal implementation**

Add the official font file and license notice under `Assets/Fonts/`. Ensure the existing Avalonia asset glob and publish/build settings preserve them in packaged output.

**Step 4: Run verification to confirm packaging**

Run: `dotnet build src/NovaTerminal.App/NovaTerminal.App.csproj -c Release`

Expected: PASS, with bundled font resources available in the app assembly/output.

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Assets/Fonts src/NovaTerminal.App/NovaTerminal.App.csproj
git commit -m "chore: bundle cascadia mono pl font assets"
```

### Task 3: Register the bundled font at startup

**Files:**
- Modify: `src/NovaTerminal.App/Program.cs`
- Modify: `src/NovaTerminal.App/App.axaml.cs`
- Create: `src/NovaTerminal.App/Core/BundledFontRegistration.cs`
- Test: `tests/NovaTerminal.Tests/App/BundledFontRegistrationTests.cs`

**Step 1: Write the failing test**

Add a focused test for the registration helper that exposes the bundled font family name and asset URI(s) expected at startup. Keep the test on helper behavior rather than trying to exercise Avalonia rendering.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~BundledFontRegistrationTests`

Expected: FAIL because the helper does not exist.

**Step 3: Write minimal implementation**

Create a small app-level helper that registers the bundled Cascadia font collection with Avalonia during startup. Keep failure handling conservative: if registration fails, log and let the app fall back to existing font resolution.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~BundledFontRegistrationTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Program.cs src/NovaTerminal.App/App.axaml.cs src/NovaTerminal.App/Core/BundledFontRegistration.cs tests/NovaTerminal.Tests/App/BundledFontRegistrationTests.cs
git commit -m "feat: register bundled cascadia mono pl font"
```

### Task 4: Keep the settings UI aligned with the bundled font

**Files:**
- Modify: `src/NovaTerminal.App/SettingsWindow.axaml.cs`
- Test: `tests/NovaTerminal.Tests/App/SettingsWindowFontListTests.cs`

**Step 1: Write the failing test**

Add a deterministic test around the font-list preparation logic so the configured bundled family remains selectable even if it is not returned from the raw system font enumeration.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~SettingsWindowFontListTests`

Expected: FAIL because the settings UI only trusts the raw font manager list.

**Step 3: Write minimal implementation**

Refactor only the font-list assembly logic needed to inject the bundled family name when absent and preserve the current selection flow.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~SettingsWindowFontListTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/SettingsWindow.axaml.cs tests/NovaTerminal.Tests/App/SettingsWindowFontListTests.cs
git commit -m "fix: keep bundled font visible in settings"
```

### Task 5: Run end-to-end verification

**Files:**
- Verify only

**Step 1: Run focused tests**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~TerminalSettingsTests|FullyQualifiedName~BundledFontRegistrationTests|FullyQualifiedName~SettingsWindowFontListTests`

Expected: PASS

**Step 2: Run app build**

Run: `dotnet build NovaTerminal.sln -c Release`

Expected: PASS

**Step 3: Manual smoke check**

Launch the app with a fresh settings file and confirm the first-run terminal uses `Cascadia Mono PL` and Powerline prompt glyphs render cleanly.

**Step 4: Commit**

```bash
git add -A
git commit -m "feat: ship cascadia mono pl as first-run default"
```
