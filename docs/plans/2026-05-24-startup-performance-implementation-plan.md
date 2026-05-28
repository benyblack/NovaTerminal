# Startup Performance Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reduce startup latency for first window shown, first terminal ready, and full restore complete by moving NovaTerminal to staged startup with progressive restore and explicit before/after measurement support.

**Architecture:** Keep the change in `NovaTerminal.App` and shared app metrics code. Add a startup-phase tracker, move heavy launch work out of the initial constructor path, restore the selected tab first, restore background tabs progressively, and emit structured startup metrics that support baseline and post-change comparison reports.

**Tech Stack:** .NET 10, Avalonia 12, NovaTerminal app startup path, existing `RendererStatistics`, xUnit, PowerShell measurement scripts or test tools, deterministic app-layer tests

---

### Task 1: Add startup metrics coverage to the existing collector

**Files:**
- Modify: `src/NovaTerminal.Rendering/RendererStatistics.cs`
- Modify: `tests/NovaTerminal.Tests/RenderTests/RendererMetricsTests.cs`

**Step 1: Write the failing test**

Add tests that record startup-phase timings and assert the new counters and summary fields are exposed by `RendererStatistics`.

Cover:

- window shown timing
- first terminal ready timing
- session restore complete timing

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~RendererMetricsTests`

Expected: FAIL because the startup metrics API does not exist yet.

**Step 3: Write minimal implementation**

Extend `RendererStatistics` with startup timing fields, record methods, reset support, and report output. Keep the API additive and thread-safe.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~RendererMetricsTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.Rendering/RendererStatistics.cs tests/NovaTerminal.Tests/RenderTests/RendererMetricsTests.cs
git commit -m "feat: add startup metrics to renderer statistics"
```

### Task 2: Introduce a startup-phase tracker

**Files:**
- Create: `src/NovaTerminal.App/Core/StartupPerformanceTracker.cs`
- Modify: `src/NovaTerminal.App/Program.cs`
- Modify: `src/NovaTerminal.App/App.axaml.cs`
- Test: `tests/NovaTerminal.Tests/App/StartupPerformanceTrackerTests.cs`

**Step 1: Write the failing test**

Add tests for a small tracker that:

- captures startup begin time
- records named milestones once
- computes elapsed times consistently
- ignores duplicate milestone writes safely

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~StartupPerformanceTrackerTests`

Expected: FAIL because the tracker does not exist.

**Step 3: Write minimal implementation**

Create an app-level tracker that starts in `Program.Main`, records constructor/open milestones, and forwards the elapsed times to `RendererStatistics`.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~StartupPerformanceTrackerTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/StartupPerformanceTracker.cs src/NovaTerminal.App/Program.cs src/NovaTerminal.App/App.axaml.cs tests/NovaTerminal.Tests/App/StartupPerformanceTrackerTests.cs
git commit -m "feat: add startup phase tracker"
```

### Task 3: Stop per-pane settings reload during startup

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Modify: `src/NovaTerminal.App/Core/SessionManager.cs`
- Test: `tests/NovaTerminal.Tests/App/TerminalPaneSettingsTests.cs`

**Step 1: Write the failing test**

Add a deterministic test that proves `TerminalPane` can be initialized with shared settings and does not need to call `TerminalSettings.Load()` from pane setup.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~TerminalPaneSettingsTests`

Expected: FAIL because pane setup still owns settings loading.

**Step 3: Write minimal implementation**

Refactor `TerminalPane` initialization to accept already-loaded settings from `MainWindow` or restore code. Keep the public surface explicit and minimal.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~TerminalPaneSettingsTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml.cs src/NovaTerminal.App/MainWindow.axaml.cs src/NovaTerminal.App/Core/SessionManager.cs tests/NovaTerminal.Tests/App/TerminalPaneSettingsTests.cs
git commit -m "refactor: share startup settings across terminal panes"
```

### Task 4: Extract a progressive restore plan from session data

**Files:**
- Create: `src/NovaTerminal.App/Core/StartupRestorePlan.cs`
- Modify: `src/NovaTerminal.App/Core/SessionManager.cs`
- Test: `tests/NovaTerminal.Tests/App/StartupRestorePlanTests.cs`

**Step 1: Write the failing test**

Add tests that build a restore plan from a saved session and assert:

- selected tab is first
- active pane in the selected tab is prioritized
- background tabs remain in stable restore order

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~StartupRestorePlanTests`

Expected: FAIL because no progressive restore planning seam exists.

**Step 3: Write minimal implementation**

Add a small restore-plan model and helper logic that can describe the startup order without immediately creating every pane and session.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~StartupRestorePlanTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/StartupRestorePlan.cs src/NovaTerminal.App/Core/SessionManager.cs tests/NovaTerminal.Tests/App/StartupRestorePlanTests.cs
git commit -m "feat: add progressive startup restore planning"
```

### Task 5: Add a progressive restore coordinator

**Files:**
- Create: `src/NovaTerminal.App/Core/StartupRestoreCoordinator.cs`
- Modify: `src/NovaTerminal.App/Core/SessionManager.cs`
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Test: `tests/NovaTerminal.Tests/App/StartupRestoreCoordinatorTests.cs`

**Step 1: Write the failing test**

Add tests for a coordinator that:

- restores the selected tab immediately
- defers background tab hydration
- marks restore complete only after all deferred work finishes
- preserves stable sequencing and completion callbacks

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~StartupRestoreCoordinatorTests`

Expected: FAIL because the coordinator does not exist.

**Step 3: Write minimal implementation**

Create a narrow app-layer coordinator that consumes the restore plan, schedules background tab restoration on the dispatcher, and records completion timing.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~StartupRestoreCoordinatorTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/StartupRestoreCoordinator.cs src/NovaTerminal.App/Core/SessionManager.cs src/NovaTerminal.App/MainWindow.axaml.cs tests/NovaTerminal.Tests/App/StartupRestoreCoordinatorTests.cs
git commit -m "feat: coordinate progressive startup restore"
```

### Task 6: Shrink the `MainWindow` critical startup path

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`
- Modify: `src/NovaTerminal.App/App.axaml.cs`
- Test: `tests/NovaTerminal.Tests/App/MainWindowStartupTests.cs`

**Step 1: Write the failing test**

Add focused tests around extracted startup orchestration logic so `MainWindow` can:

- separate immediate startup work from deferred work
- queue non-critical initialization after open
- preserve selected-tab focus and active-pane behavior

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~MainWindowStartupTests`

Expected: FAIL because startup work is still constructor-heavy and not separable.

**Step 3: Write minimal implementation**

Refactor `MainWindow` startup flow so the constructor builds only what the first frame needs, then uses deferred startup hooks for the rest.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~MainWindowStartupTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs src/NovaTerminal.App/App.axaml.cs tests/NovaTerminal.Tests/App/MainWindowStartupTests.cs
git commit -m "refactor: stage main window startup work"
```

### Task 7: Delay non-critical pane startup work

**Files:**
- Modify: `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs`
- Test: `tests/NovaTerminal.Tests/CommandAssist/TerminalPaneDeferredStartupTests.cs`

**Step 1: Write the failing test**

Add tests proving:

- the first active pane can initialize immediately
- background panes can delay optional startup work
- deferred assist-related startup does not break feature enablement once activated

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~TerminalPaneDeferredStartupTests`

Expected: FAIL because pane startup still initializes all optional work immediately.

**Step 3: Write minimal implementation**

Add a small startup mode or explicit initialization path so optional pane subsystems can be delayed for non-primary startup panes without changing terminal core behavior.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~TerminalPaneDeferredStartupTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Controls/TerminalPane.axaml.cs src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs tests/NovaTerminal.Tests/CommandAssist/TerminalPaneDeferredStartupTests.cs
git commit -m "perf: defer non-critical pane startup work"
```

### Task 8: Add structured startup metrics artifact output

**Files:**
- Create: `src/NovaTerminal.App/Core/StartupMetricsWriter.cs`
- Modify: `src/NovaTerminal.App/Core/StartupPerformanceTracker.cs`
- Test: `tests/NovaTerminal.Tests/App/StartupMetricsWriterTests.cs`

**Step 1: Write the failing test**

Add tests that verify an opt-in metrics writer can:

- emit one structured record per launch
- include all required startup phase timings
- fail safely when output is unavailable

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~StartupMetricsWriterTests`

Expected: FAIL because no startup metrics artifact writer exists.

**Step 3: Write minimal implementation**

Create a simple opt-in writer controlled by environment variable or equivalent configuration. Emit per-launch startup metrics in JSON or JSONL so repeated baseline and post-change runs can be summarized later.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~StartupMetricsWriterTests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/NovaTerminal.App/Core/StartupMetricsWriter.cs src/NovaTerminal.App/Core/StartupPerformanceTracker.cs tests/NovaTerminal.Tests/App/StartupMetricsWriterTests.cs
git commit -m "feat: emit structured startup metrics artifacts"
```

### Task 9: Add repeatable baseline and comparison tooling

**Files:**
- Create: `tests/tools/measure_startup.ps1`
- Create: `tests/tools/summarize_startup_metrics.ps1`
- Create: `docs/performance/startup-measurement.md`
- Test: `tests/NovaTerminal.Tests/App/StartupMetricsSummaryTests.cs`

**Step 1: Write the failing test**

Add a focused test for summary logic that consumes structured startup metric records and computes:

- count
- average or median by phase
- delta between baseline and candidate runs
- percentage improvement

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~StartupMetricsSummaryTests`

Expected: FAIL because no startup summary logic exists.

**Step 3: Write minimal implementation**

Add a repeatable measurement script for N launches, a summary script for before/after comparison, and concise documentation describing the protocol. Keep the scripts simple and local-first.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~StartupMetricsSummaryTests`

Expected: PASS

**Step 5: Commit**

```bash
git add tests/tools/measure_startup.ps1 tests/tools/summarize_startup_metrics.ps1 docs/performance/startup-measurement.md tests/NovaTerminal.Tests/App/StartupMetricsSummaryTests.cs
git commit -m "feat: add startup measurement and comparison tooling"
```

### Task 10: Capture baseline before behavioral changes

**Files:**
- Verify only

**Step 1: Build the app in the chosen configuration**

Run: `dotnet build NovaTerminal.sln -c Release`

Expected: PASS

**Step 2: Run baseline startup measurement**

Run: `pwsh -File tests/tools/measure_startup.ps1 -Configuration Release -Label baseline -Iterations 10`

Expected: PASS, producing a baseline startup metrics artifact.

**Step 3: Summarize the baseline**

Run: `pwsh -File tests/tools/summarize_startup_metrics.ps1 -Input artifacts-codex/startup/baseline`

Expected: PASS, with baseline timings for first window shown, first terminal ready, and restore complete.

**Step 4: Commit**

```bash
git add docs/performance/startup-measurement.md tests/tools/measure_startup.ps1 tests/tools/summarize_startup_metrics.ps1
git commit -m "docs: lock startup baseline measurement workflow"
```

### Task 11: Verify improved behavior and produce the enhancement report

**Files:**
- Create: `artifacts-codex/startup-performance-report.md`
- Verify only

**Step 1: Run focused startup tests**

Run: `dotnet test tests/NovaTerminal.Tests/NovaTerminal.Tests.csproj -c Release --filter FullyQualifiedName~RendererMetricsTests|FullyQualifiedName~StartupPerformanceTrackerTests|FullyQualifiedName~TerminalPaneSettingsTests|FullyQualifiedName~StartupRestorePlanTests|FullyQualifiedName~StartupRestoreCoordinatorTests|FullyQualifiedName~MainWindowStartupTests|FullyQualifiedName~TerminalPaneDeferredStartupTests|FullyQualifiedName~StartupMetricsWriterTests|FullyQualifiedName~StartupMetricsSummaryTests`

Expected: PASS

**Step 2: Run full solution build**

Run: `dotnet build NovaTerminal.sln -c Release`

Expected: PASS

**Step 3: Run post-change startup measurement**

Run: `pwsh -File tests/tools/measure_startup.ps1 -Configuration Release -Label candidate -Iterations 10`

Expected: PASS, producing candidate startup metrics artifacts.

**Step 4: Generate comparison summary**

Run: `pwsh -File tests/tools/summarize_startup_metrics.ps1 -Baseline artifacts-codex/startup/baseline -Candidate artifacts-codex/startup/candidate -Out artifacts-codex/startup-performance-report.md`

Expected: PASS, with concrete before/after timings and percentage improvement for each tracked startup phase.

**Step 5: Commit**

```bash
git add artifacts-codex/startup-performance-report.md
git commit -m "perf: improve startup latency and document gains"
```
