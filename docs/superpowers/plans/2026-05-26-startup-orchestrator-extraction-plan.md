# StartupOrchestrator Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract `StartupOrchestrator` from `MainWindow.axaml.cs`, introduce the `AppServiceBundle` composition-root pattern, and migrate every existing `StartupPerformanceTracker.Current?.TryMark*` call site plus the inline session-restore lifecycle behind the orchestrator. Zero net behavior change; one quiet correctness improvement (collapses a duplicated mark-pair).

**Architecture:** Three new files in `src/NovaTerminal.App/Core/` (`StartupOrchestrator.cs`, `AppServiceBundle.cs`, `AppServices.cs`). MainWindow gains a typed ctor `MainWindow(AppServiceBundle services)` and a parameterless forwarder for the XAML designer + existing tests. The orchestrator wraps the existing `StartupPerformanceTracker` and owns the existing `StartupRestoreCoordinator` instance; neither type is changed.

**Tech Stack:** C# 12, .NET 10, Avalonia 12, xUnit (with `Avalonia.Headless.XUnit` for headless tests), `scripts/build.{ps1,sh}` wrappers (raw `dotnet` hangs Bash pipes — see `CLAUDE.md`).

**Spec:** `docs/superpowers/specs/2026-05-26-startup-orchestrator-extraction-design.md` (committed in `b9bc3fe`).

**Pre-existing in-repo staged changes:** the icon-ico-swap + PowerShell `-File` unquote work is in the staging area when this plan starts; do **NOT** include it in any commit produced by this plan. Use explicit paths in every `git commit` and never run `git add .` / `git add -A`.

---

### Task 1: Create `StartupOrchestrator` with failing tests, then make them pass

**Files:**
- Create: `src/NovaTerminal.App/Core/StartupOrchestrator.cs`
- Create: `tests/NovaTerminal.Tests/Core/StartupOrchestratorTests.cs`

- [ ] **Step 1: Write the failing test file**

Create `tests/NovaTerminal.Tests/Core/StartupOrchestratorTests.cs` with the full test class:

```csharp
using System;
using System.Collections.Generic;
using NovaTerminal.Platform;
using Xunit;

namespace NovaTerminal.Tests.Core;

public sealed class StartupOrchestratorTests
{
    private static (StartupPerformanceTracker tracker, long[] clock) CreateTracker()
    {
        var clock = new long[] { 0L };
        Func<long> ticks = () => clock[0];
        var tracker = (StartupPerformanceTracker)Activator.CreateInstance(
            typeof(StartupPerformanceTracker),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new object?[] { ticks, 0L, 1000L, null },
            null)!;
        return (tracker, clock);
    }

    private static StartupRestoreCoordinator CreateInlineCoordinator()
        => new(action => action());

    private static StartupRestoreCoordinator CreateCapturingCoordinator(List<Action> captured)
        => new(action => captured.Add(action));

    private static NovaSession SessionWith(int tabCount, int activeIndex)
    {
        var session = new NovaSession { ActiveTabIndex = activeIndex };
        for (int i = 0; i < tabCount; i++)
        {
            session.Tabs.Add(new TabSession { Title = $"tab-{i}" });
        }
        return session;
    }

    [Fact]
    public void Mark_DelegatesToTracker()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());

        orchestrator.Mark(StartupPhase.WindowOpened);

        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.WindowOpened, out _));
    }

    [Fact]
    public void Checkpoint_DelegatesToTracker()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());

        orchestrator.Checkpoint("MainWindow.AfterApplyTheme");

        var snapshot = tracker.CreateSnapshot();
        Assert.NotNull(snapshot.Checkpoints);
        Assert.True(snapshot.Checkpoints!.ContainsKey("MainWindow.AfterApplyTheme"));
    }

    [Fact]
    public void BeginSessionRestore_WithDeferredTabs_CallsMaterializeImmediateOnce_AndStashesPlan()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var session = SessionWith(tabCount: 3, activeIndex: 1);
        var materialized = new List<int>();

        orchestrator.BeginSessionRestore(session, immediate => materialized.Add(immediate.OriginalIndex));

        Assert.Equal(new[] { 1 }, materialized);
        Assert.True(orchestrator.HasPendingDeferredRestore);
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.SessionRestoreComplete, out _));
        Assert.False(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
    }

    [Fact]
    public void BeginSessionRestore_WithSingleTab_MarksBothPhasesAndClearsPending()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var session = SessionWith(tabCount: 1, activeIndex: 0);
        var materialized = new List<int>();

        orchestrator.BeginSessionRestore(session, immediate => materialized.Add(immediate.OriginalIndex));

        Assert.Equal(new[] { 0 }, materialized);
        Assert.False(orchestrator.HasPendingDeferredRestore);
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.SessionRestoreComplete, out _));
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
    }

    [Fact]
    public void Constructor_ThrowsOnNullDependencies()
    {
        var (tracker, _) = CreateTracker();
        var coordinator = CreateInlineCoordinator();

        Assert.Throws<ArgumentNullException>(() => new StartupOrchestrator(null!, coordinator));
        Assert.Throws<ArgumentNullException>(() => new StartupOrchestrator(tracker, null!));
    }

    [Fact]
    public void BeginSessionRestore_ThrowsOnNullArguments()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var session = SessionWith(tabCount: 1, activeIndex: 0);

        Assert.Throws<ArgumentNullException>(() => orchestrator.BeginSessionRestore(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => orchestrator.BeginSessionRestore(session, null!));
    }

    [Fact]
    public void DrainDeferred_ThrowsOnNullArgument()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());

        Assert.Throws<ArgumentNullException>(() => orchestrator.DrainDeferred(null!));
    }

    [Fact]
    public void BeginSessionRestore_WhenMaterializeThrows_PropagatesAndLeavesStateUnchanged()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var session = SessionWith(tabCount: 3, activeIndex: 1);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            orchestrator.BeginSessionRestore(session, _ => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", ex.Message);
        Assert.False(orchestrator.HasPendingDeferredRestore);
        Assert.False(tracker.TryGetElapsedMilliseconds(StartupPhase.SessionRestoreComplete, out _));
        Assert.False(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
    }

    [Fact]
    public void CompleteWithoutRestore_MarksBothPhases()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());

        orchestrator.CompleteWithoutRestore();

        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.SessionRestoreComplete, out _));
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
    }

    [Fact]
    public void CompleteWithoutRestore_IsIdempotent()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());

        orchestrator.CompleteWithoutRestore();
        orchestrator.CompleteWithoutRestore();

        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.SessionRestoreComplete, out _));
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
    }

    [Fact]
    public void DrainDeferred_WithPendingPlan_RunsAllDeferredTabsInOriginalOrder_AndMarksBackground()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var session = SessionWith(tabCount: 4, activeIndex: 1);
        orchestrator.BeginSessionRestore(session, _ => { });
        var hydrated = new List<int>();

        orchestrator.DrainDeferred(tab => hydrated.Add(tab.OriginalIndex));

        Assert.Equal(new[] { 0, 2, 3 }, hydrated);
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
        Assert.False(orchestrator.HasPendingDeferredRestore);
    }

    [Fact]
    public void DrainDeferred_WithoutPendingPlan_IsNoOp()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var hydrated = new List<int>();

        orchestrator.DrainDeferred(tab => hydrated.Add(tab.OriginalIndex));

        Assert.Empty(hydrated);
        Assert.False(tracker.TryGetElapsedMilliseconds(StartupPhase.SessionRestoreComplete, out _));
        Assert.False(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
    }

    [Fact]
    public void DrainDeferred_WhenOneTabThrows_StillCompletesAndMarksBackground()
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var session = SessionWith(tabCount: 4, activeIndex: 1);
        orchestrator.BeginSessionRestore(session, _ => { });
        var logged = new List<string>();
        Action<string>? previous = TerminalLogger.OnLog;
        TerminalLogger.OnLog = logged.Add;
        var hydrated = new List<int>();

        try
        {
            orchestrator.DrainDeferred(tab =>
            {
                if (tab.OriginalIndex == 2)
                {
                    throw new InvalidOperationException("bad-tab");
                }
                hydrated.Add(tab.OriginalIndex);
            });
        }
        finally
        {
            TerminalLogger.OnLog = previous;
        }

        Assert.Equal(new[] { 0, 3 }, hydrated);
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
        Assert.False(orchestrator.HasPendingDeferredRestore);
        Assert.Contains(logged, line => line.Contains("bad-tab", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(5, 3)]
    public void Invariant_AfterBeginSessionRestore_BackgroundCompleteImpliesNoPendingPlan(int tabCount, int activeIndex)
    {
        var (tracker, _) = CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var session = SessionWith(tabCount, activeIndex);

        orchestrator.BeginSessionRestore(session, _ => { });

        bool backgroundComplete = tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _);
        Assert.Equal(backgroundComplete, !orchestrator.HasPendingDeferredRestore);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (compile error — `StartupOrchestrator` does not exist)**

Run:

```powershell
scripts/build.ps1 test -c Release --filter "FullyQualifiedName~StartupOrchestratorTests" --no-restore
```

Expected: build failure with `error CS0246: The type or namespace name 'StartupOrchestrator' could not be found`.

- [ ] **Step 3: Create the orchestrator class**

Create `src/NovaTerminal.App/Core/StartupOrchestrator.cs`:

```csharp
using System;

namespace NovaTerminal.Platform;

public sealed class StartupOrchestrator
{
    private readonly StartupPerformanceTracker _tracker;
    private readonly StartupRestoreCoordinator _coordinator;
    private StartupRestorePlan? _pendingPlan;

    public StartupOrchestrator(
        StartupPerformanceTracker tracker,
        StartupRestoreCoordinator coordinator)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public bool HasPendingDeferredRestore => _pendingPlan is not null;

    public void Mark(StartupPhase phase) => _tracker.TryMark(phase);

    public void Checkpoint(string name) => _tracker.TryMarkCheckpoint(name);

    public void BeginSessionRestore(
        NovaSession session,
        Action<StartupRestoreTab> materializeImmediate)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(materializeImmediate);

        var plan = StartupRestorePlan.Create(session);
        materializeImmediate(plan.ImmediateTab);

        _tracker.TryMark(StartupPhase.SessionRestoreComplete);
        if (plan.DeferredTabs.Count == 0)
        {
            _tracker.TryMark(StartupPhase.BackgroundRestoreComplete);
        }
        else
        {
            _pendingPlan = plan;
        }
    }

    public void DrainDeferred(Action<StartupRestoreTab> materializeTab)
    {
        ArgumentNullException.ThrowIfNull(materializeTab);

        var plan = _pendingPlan;
        if (plan is null)
        {
            return;
        }

        _pendingPlan = null;
        _coordinator.RunDeferred(
            plan.DeferredTabs,
            tab =>
            {
                try
                {
                    materializeTab(tab);
                }
                catch (Exception ex)
                {
                    TerminalLogger.Log($"StartupOrchestrator: deferred tab {tab.OriginalIndex} threw: {ex}");
                }
            },
            () => _tracker.TryMark(StartupPhase.BackgroundRestoreComplete));
    }

    public void CompleteWithoutRestore()
    {
        _tracker.TryMark(StartupPhase.SessionRestoreComplete);
        _tracker.TryMark(StartupPhase.BackgroundRestoreComplete);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:

```powershell
scripts/build.ps1 test -c Release --filter "FullyQualifiedName~StartupOrchestratorTests" --no-restore
```

Expected: all 15 tests pass (`Mark_DelegatesToTracker`, `Checkpoint_DelegatesToTracker`, `Constructor_ThrowsOnNullDependencies`, `BeginSessionRestore_ThrowsOnNullArguments`, `DrainDeferred_ThrowsOnNullArgument`, `BeginSessionRestore_WithDeferredTabs_...`, `BeginSessionRestore_WithSingleTab_...`, `BeginSessionRestore_WhenMaterializeThrows_...`, `CompleteWithoutRestore_MarksBothPhases`, `CompleteWithoutRestore_IsIdempotent`, `DrainDeferred_WithPendingPlan_...`, `DrainDeferred_WithoutPendingPlan_IsNoOp`, `DrainDeferred_WhenOneTabThrows_...`, 4× `Invariant_...`).

- [ ] **Step 5: Commit**

```powershell
git commit src/NovaTerminal.App/Core/StartupOrchestrator.cs tests/NovaTerminal.Tests/Core/StartupOrchestratorTests.cs -m "feat: add StartupOrchestrator with phase + restore lifecycle"
```

---

### Task 2: Create `AppServiceBundle` and `AppServices` with tests

**Files:**
- Create: `src/NovaTerminal.App/Core/AppServiceBundle.cs`
- Create: `src/NovaTerminal.App/Core/AppServices.cs`
- Create: `tests/NovaTerminal.Tests/Core/AppServicesTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/NovaTerminal.Tests/Core/AppServicesTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NovaTerminal.Platform;
using Xunit;

namespace NovaTerminal.Tests.Core;

public sealed class AppServicesTests
{
    [Fact]
    public void Build_ReturnsBundleWithWiredOrchestrator()
    {
        var clock = new long[] { 0L };
        var tracker = (StartupPerformanceTracker)Activator.CreateInstance(
            typeof(StartupPerformanceTracker),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new object?[] { (Func<long>)(() => clock[0]), 0L, 1000L, null },
            null)!;

        var bundle = AppServices.Build(tracker, schedule: action => action());

        Assert.NotNull(bundle.Startup);
        bundle.Startup.Mark(StartupPhase.WindowOpened);
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.WindowOpened, out _));
    }

    [Fact]
    public void BuildForDesigner_ReturnsBundleWithSynchronousScheduler()
    {
        var bundle = AppServices.BuildForDesigner();
        var session = new NovaSession { ActiveTabIndex = 0 };
        session.Tabs.Add(new TabSession { Title = "a" });
        session.Tabs.Add(new TabSession { Title = "b" });
        bundle.Startup.BeginSessionRestore(session, _ => { });
        var hydrated = new List<int>();

        bundle.Startup.DrainDeferred(tab => hydrated.Add(tab.OriginalIndex));

        Assert.Equal(new[] { 1 }, hydrated);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (compile error — `AppServices` does not exist)**

Run:

```powershell
scripts/build.ps1 test -c Release --filter "FullyQualifiedName~AppServicesTests" --no-restore
```

Expected: build failure with `error CS0103: The name 'AppServices' does not exist`.

- [ ] **Step 3: Create the bundle record**

Create `src/NovaTerminal.App/Core/AppServiceBundle.cs`:

```csharp
namespace NovaTerminal.Platform;

public sealed record AppServiceBundle(StartupOrchestrator Startup);
```

- [ ] **Step 4: Create the services factory**

Create `src/NovaTerminal.App/Core/AppServices.cs`:

```csharp
using System;

namespace NovaTerminal.Platform;

public static class AppServices
{
    public static AppServiceBundle Build(
        StartupPerformanceTracker tracker,
        Action<Action> schedule)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(schedule);

        var coordinator = new StartupRestoreCoordinator(schedule);
        var orchestrator = new StartupOrchestrator(tracker, coordinator);
        return new AppServiceBundle(orchestrator);
    }

    public static AppServiceBundle BuildForDesigner()
    {
        var tracker = new StartupPerformanceTracker();
        var coordinator = new StartupRestoreCoordinator(action => action());
        var orchestrator = new StartupOrchestrator(tracker, coordinator);
        return new AppServiceBundle(orchestrator);
    }
}
```

Note: `BuildForDesigner` is `public` (not `internal`) so test fixtures can call it without `InternalsVisibleTo` gymnastics.

- [ ] **Step 5: Run tests to verify they pass**

Run:

```powershell
scripts/build.ps1 test -c Release --filter "FullyQualifiedName~AppServicesTests" --no-restore
```

Expected: both tests pass.

- [ ] **Step 6: Commit**

```powershell
git commit src/NovaTerminal.App/Core/AppServiceBundle.cs src/NovaTerminal.App/Core/AppServices.cs tests/NovaTerminal.Tests/Core/AppServicesTests.cs -m "feat: add AppServiceBundle composition-root pattern"
```

---

### Task 3: Wire `MainWindow` to receive `AppServiceBundle`, no call-site migration yet

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (add typed ctor, forwarder, field)
- Modify: `src/NovaTerminal.App/App.axaml.cs` (use `AppServices.Build`)
- Create: `tests/NovaTerminal.Tests/Core/TestMainWindowFactory.cs`
- Modify: `tests/NovaTerminal.Tests/Core/MainWindowStartupTests.cs` (use factory)

This task adds the wiring but does **not** migrate any `StartupPerformanceTracker.Current?.TryMark*` calls. After this task the orchestrator exists on `MainWindow` but no MainWindow code uses it yet. Everything still compiles and passes.

- [ ] **Step 1: Add the `_startup` field and typed ctor to `MainWindow.axaml.cs`**

Locate the existing field declarations near line 87 in `src/NovaTerminal.App/MainWindow.axaml.cs`:

```csharp
        private readonly StartupRestoreCoordinator _startupRestoreCoordinator;
        private StartupRestorePlan? _pendingStartupRestorePlan;
```

Add a new field directly above them:

```csharp
        private readonly StartupOrchestrator _startup;
        private readonly StartupRestoreCoordinator _startupRestoreCoordinator;
        private StartupRestorePlan? _pendingStartupRestorePlan;
```

Find the existing parameterless constructor `public MainWindow()`. Convert it into two constructors. The typed one becomes the real ctor; the parameterless one becomes a forwarder kept ONLY for the XAML designer and the existing reflection-driven tests:

```csharp
        // Designer + legacy-test forwarder. Production callers must use the
        // typed ctor via App.OnFrameworkInitializationCompleted.
        public MainWindow() : this(AppServices.BuildForDesigner())
        {
        }

        public MainWindow(AppServiceBundle services)
        {
            ArgumentNullException.ThrowIfNull(services);
            _startup = services.Startup;
            // ... rest of the existing ctor body unchanged ...
        }
```

Concretely: copy the existing ctor body verbatim into the typed ctor, then leave the parameterless ctor as the one-line forwarder shown above. Do NOT delete or change any existing line of ctor body in this step.

- [ ] **Step 2: Update `App.axaml.cs` to construct services and pass the bundle**

Replace the body of `OnFrameworkInitializationCompleted` in `src/NovaTerminal.App/App.axaml.cs`:

```csharp
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var tracker = StartupPerformanceTracker.Current
                ?? throw new InvalidOperationException(
                    "StartupPerformanceTracker.StartNewCurrent must run before App init.");

            var services = AppServices.Build(
                tracker,
                schedule: action => Avalonia.Threading.Dispatcher.UIThread.Post(
                    action,
                    Avalonia.Threading.DispatcherPriority.Background));

            desktop.MainWindow = new MainWindow(services);
            services.Startup.Mark(StartupPhase.MainWindowConstructed);

            // Enable DevTools for debugging - Press F12 to open
#if DEBUG
            this.AttachDeveloperTools();
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }
```

Add the using directive at the top of the file if not already present:

```csharp
using System;
```

- [ ] **Step 3: Create the test factory**

Create `tests/NovaTerminal.Tests/Core/TestMainWindowFactory.cs`:

```csharp
using NovaTerminal.Platform;

namespace NovaTerminal.Tests.Core;

internal static class TestMainWindowFactory
{
    public static NovaTerminal.MainWindow Create()
        => new NovaTerminal.MainWindow(AppServices.BuildForDesigner());
}
```

- [ ] **Step 4: Switch existing MainWindow fixture call sites to use the factory**

In `tests/NovaTerminal.Tests/Core/MainWindowStartupTests.cs`, replace every occurrence of `new NovaTerminal.MainWindow()` with `TestMainWindowFactory.Create()`. There are 7 occurrences on lines 15, 23, 37, 65, 83, 161, 195, 221. Use the Edit tool with `replace_all: true`:

```
old_string: new NovaTerminal.MainWindow()
new_string: TestMainWindowFactory.Create()
```

Then add the using directive at the top of the test file if not already present:

```csharp
using NovaTerminal.Platform;
```

(The `NovaTerminal.Platform` namespace contains `AppServices` and the orchestrator; the factory file in the same namespace doesn't need a using directive itself.)

The `RecordingCommandProbeWindow` private nested class at the bottom of the file derives from `NovaTerminal.MainWindow` with no explicit ctor — it inherits the parameterless ctor unchanged. Leave that class alone.

- [ ] **Step 5: Build and run the affected tests**

Run:

```powershell
scripts/build.ps1 test -c Release --filter "FullyQualifiedName~MainWindowStartupTests|FullyQualifiedName~StartupOrchestratorTests|FullyQualifiedName~AppServicesTests" --no-restore
```

Expected: all tests pass. If any `MainWindowStartupTests` test fails because of a reflection-driven assertion that depended on the parameterless ctor running, investigate — the typed ctor body is supposed to be identical to the old one. Do not "fix" the test by reverting; fix the ctor body.

- [ ] **Step 6: Build the App project to verify App.axaml.cs compiles under AOT-style settings**

Run:

```powershell
scripts/build.ps1 build src/NovaTerminal.App/NovaTerminal.App.csproj -c Release --no-restore
```

Expected: build succeeds. The App project's csproj has `<PublishAot>true</PublishAot>` but `dotnet build` (not `publish`) does not enforce AOT trim warnings; we still want a clean build here.

- [ ] **Step 7: Commit**

```powershell
git commit src/NovaTerminal.App/MainWindow.axaml.cs src/NovaTerminal.App/App.axaml.cs tests/NovaTerminal.Tests/Core/TestMainWindowFactory.cs tests/NovaTerminal.Tests/Core/MainWindowStartupTests.cs -m "refactor: wire MainWindow through AppServiceBundle composition root"
```

---

### Task 4: Migrate `Mark` and `Checkpoint` call sites in `MainWindow.axaml.cs`

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`

Pure mechanical refactor. Every `StartupPerformanceTracker.Current?.TryMark*(...)` call inside MainWindow becomes `_startup.Mark(...)` or `_startup.Checkpoint(...)`. No behavior change.

Existing call sites (line numbers as of `HEAD` at start of this plan):

| Line | Current | Replace with |
|---|---|---|
| 117 | `StartupPerformanceTracker.Current?.TryMark(StartupPhase.WindowOpened);` | `_startup.Mark(StartupPhase.WindowOpened);` |
| 1217 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("StartupRestore.AfterSessionLoad");` | `_startup.Checkpoint("StartupRestore.AfterSessionLoad");` |
| 1234 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("StartupRestore.AfterTabMaterialization");` | `_startup.Checkpoint("StartupRestore.AfterTabMaterialization");` |
| 1247 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("StartupRestore.AfterInitializeRestoredTabs");` | `_startup.Checkpoint("StartupRestore.AfterInitializeRestoredTabs");` |
| 1248 | `StartupPerformanceTracker.Current?.TryMark(StartupPhase.SessionRestoreComplete);` | `_startup.Mark(StartupPhase.SessionRestoreComplete);` |
| 1252 | `StartupPerformanceTracker.Current?.TryMark(StartupPhase.BackgroundRestoreComplete);` | `_startup.Mark(StartupPhase.BackgroundRestoreComplete);` |
| 1293 | `() => StartupPerformanceTracker.Current?.TryMark(StartupPhase.BackgroundRestoreComplete));` | `() => _startup.Mark(StartupPhase.BackgroundRestoreComplete));` |
| 1967 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("MainWindow.AfterInitializeComponent");` | `_startup.Checkpoint("MainWindow.AfterInitializeComponent");` |
| 1969 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("MainWindow.AfterSettingsLoad");` | `_startup.Checkpoint("MainWindow.AfterSettingsLoad");` |
| 1979 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("MainWindow.AfterLegacyMigration");` | `_startup.Checkpoint("MainWindow.AfterLegacyMigration");` |
| 1987 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("MainWindow.LoadedPostStart");` | `_startup.Checkpoint("MainWindow.LoadedPostStart");` |
| 1996 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("MainWindow.LoadedPostUiReady");` | `_startup.Checkpoint("MainWindow.LoadedPostUiReady");` |
| 1997 | `StartupPerformanceTracker.Current?.TryMark(StartupPhase.DeferredWorkComplete);` | `_startup.Mark(StartupPhase.DeferredWorkComplete);` |
| 2100 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("MainWindow.AfterCoreUiWireup");` | `_startup.Checkpoint("MainWindow.AfterCoreUiWireup");` |
| 2103 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("MainWindow.AfterApplyTheme");` | `_startup.Checkpoint("MainWindow.AfterApplyTheme");` |
| 2153 | `StartupPerformanceTracker.Current?.TryMark(StartupPhase.SessionRestoreComplete);` | `_startup.Mark(StartupPhase.SessionRestoreComplete);` |
| 2154 | `StartupPerformanceTracker.Current?.TryMark(StartupPhase.BackgroundRestoreComplete);` | `_startup.Mark(StartupPhase.BackgroundRestoreComplete);` |
| 2160 | `StartupPerformanceTracker.Current?.TryMark(StartupPhase.SessionRestoreComplete);` | `_startup.Mark(StartupPhase.SessionRestoreComplete);` |
| 2161 | `StartupPerformanceTracker.Current?.TryMark(StartupPhase.BackgroundRestoreComplete);` | `_startup.Mark(StartupPhase.BackgroundRestoreComplete);` |
| 2168 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("MainWindow.AfterInitialTabs");` | `_startup.Checkpoint("MainWindow.AfterInitialTabs");` |
| 2347 | `StartupPerformanceTracker.Current?.TryMarkCheckpoint("MainWindow.CtorComplete");` | `_startup.Checkpoint("MainWindow.CtorComplete");` |
| 2585 | `StartupPerformanceTracker.Current?.TryMark(StartupPhase.FirstTerminalReady);` (inside `MainWindow.OnPaneOutputReceived` — MainWindow-owned event handler that observes TerminalPane output, NOT TerminalPane's own code) | `_startup.Mark(StartupPhase.FirstTerminalReady);` |

The spec's "TerminalPane keeps the static accessor" rule applies to calls inside `TerminalPane.axaml.cs`. MainWindow's own observer at line 2585 is in scope for migration.

- [ ] **Step 1: Apply the 22 substitutions**

Use the Edit tool with `replace_all: false` for each substitution one-by-one (most call sites have unique surrounding context). For the four duplicated patterns at lines 1248/2153/2160 and 1252/2154/2161, use larger surrounding context (3–4 lines) to disambiguate. The Edit tool will fail with a uniqueness error if the disambiguation is insufficient; if that happens, widen the context.

After every 3–5 edits, re-run a quick compile to fail fast:

```powershell
scripts/build.ps1 build src/NovaTerminal.App/NovaTerminal.App.csproj -c Release --no-restore
```

- [ ] **Step 2: Confirm zero remaining `StartupPerformanceTracker.Current?.` references in MainWindow**

Run via the Grep tool (not Bash):

```
pattern: StartupPerformanceTracker\.Current
path:    src/NovaTerminal.App/MainWindow.axaml.cs
output_mode: content
```

Expected: zero results.

- [ ] **Step 3: Run the regression test set**

Run:

```powershell
scripts/build.ps1 test -c Release --filter "FullyQualifiedName~MainWindowStartupTests|FullyQualifiedName~StartupOrchestratorTests|FullyQualifiedName~AppServicesTests|FullyQualifiedName~StartupPerformanceTrackerTests|FullyQualifiedName~StartupMetricsWriterTests|FullyQualifiedName~StartupMetricsSummaryTests|FullyQualifiedName~StartupRestoreCoordinatorTests|FullyQualifiedName~StartupRestorePlanTests|FullyQualifiedName~TerminalPaneStartupInstrumentationTests" --no-restore
```

Expected: all pass.

- [ ] **Step 4: Commit**

```powershell
git commit src/NovaTerminal.App/MainWindow.axaml.cs -m "refactor: route MainWindow phase + checkpoint marks through StartupOrchestrator"
```

---

### Task 5: Migrate session restore + deferred path through the orchestrator

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs`

This task removes `_startupRestoreCoordinator`, `_pendingStartupRestorePlan`, `RunDeferredStartupRestore()`, replaces the inline plan/mark logic with `_startup.BeginSessionRestore`/`DrainDeferred`, and collapses the duplicated fresh-start path.

- [ ] **Step 1: Delete the now-redundant fields**

In `src/NovaTerminal.App/MainWindow.axaml.cs`, near the field declarations added in Task 3, delete these two lines:

```csharp
        private readonly StartupRestoreCoordinator _startupRestoreCoordinator;
        private StartupRestorePlan? _pendingStartupRestorePlan;
```

Leave the `private readonly StartupOrchestrator _startup;` field in place. The file will not compile until later steps land.

- [ ] **Step 2: Delete the coordinator construction**

Locate line 1973 (approximate; was the only `_startupRestoreCoordinator = new ...` assignment inside the ctor body):

```csharp
            _startupRestoreCoordinator = new StartupRestoreCoordinator(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
```

Delete this line entirely. The orchestrator now owns the coordinator (constructed inside `AppServices.Build`).

- [ ] **Step 3: Rewrite the session-restore body in `TryRestoreStartupSession`**

Find the method `TryRestoreStartupSession` (around line 1208). Replace its body between the `if (!SessionManager.TryLoadSavedSession(...) || ...) return false;` guard and the closing brace. The new body:

```csharp
        private bool TryRestoreStartupSession(TabControl tabs)
        {
            if (!SessionManager.TryLoadSavedSession(out NovaSession? session) ||
                session == null ||
                session.Tabs.Count == 0)
            {
                return false;
            }
            _startup.Checkpoint("StartupRestore.AfterSessionLoad");

            _startup.BeginSessionRestore(session, immediate =>
            {
                tabs.Items.Clear();

                for (int index = 0; index < session.Tabs.Count; index++)
                {
                    TabSession tabSession = session.Tabs[index];
                    TabItem? tabItem = index == immediate.OriginalIndex
                        ? SessionManager.CreateRestoredTabItem(tabSession, _settings)
                        : CreateStartupPlaceholderTab(tabSession);

                    if (tabItem != null)
                    {
                        tabs.Items.Add(tabItem);
                    }
                }

                if (tabs.Items.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Session restore produced no tab items; aborting restore.");
                }

                if (immediate.OriginalIndex >= 0 && immediate.OriginalIndex < tabs.Items.Count)
                {
                    tabs.SelectedIndex = immediate.OriginalIndex;
                }
            });

            _startup.Checkpoint("StartupRestore.AfterTabMaterialization");

            InitializeRestoredTabs(tabs);
            _startup.Checkpoint("StartupRestore.AfterInitializeRestoredTabs");

            return true;
        }
```

Two semantic notes:

- If the callback throws (the empty-tabs guard), `_startup.BeginSessionRestore` propagates the exception. The orchestrator's contract guarantees no phases were marked and no plan was stashed. The caller of `TryRestoreStartupSession` already wraps the call in a `try/catch` flow (the outer `TryRestore...` returns false; see Step 4) — keep that flow.
- `_startup.Mark(StartupPhase.SessionRestoreComplete)` and the if/else around `_pendingStartupRestorePlan = plan` that previously lived at lines 1248–1258 are gone. The orchestrator handles them.

- [ ] **Step 4: Wrap the `BeginSessionRestore` call site to translate the throw into a clean fall-through**

The current callers of `TryRestoreStartupSession` (around line 2150) treat a `false` return value as "session restore failed; do the fresh-start path". `BeginSessionRestore` now throws when the layout callback rejects the session. Translate the throw into a `false` return inside `TryRestoreStartupSession`:

Replace the body crafted in Step 3 with this wrapped form:

```csharp
        private bool TryRestoreStartupSession(TabControl tabs)
        {
            if (!SessionManager.TryLoadSavedSession(out NovaSession? session) ||
                session == null ||
                session.Tabs.Count == 0)
            {
                return false;
            }
            _startup.Checkpoint("StartupRestore.AfterSessionLoad");

            try
            {
                _startup.BeginSessionRestore(session, immediate =>
                {
                    tabs.Items.Clear();

                    for (int index = 0; index < session.Tabs.Count; index++)
                    {
                        TabSession tabSession = session.Tabs[index];
                        TabItem? tabItem = index == immediate.OriginalIndex
                            ? SessionManager.CreateRestoredTabItem(tabSession, _settings)
                            : CreateStartupPlaceholderTab(tabSession);

                        if (tabItem != null)
                        {
                            tabs.Items.Add(tabItem);
                        }
                    }

                    if (tabs.Items.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "Session restore produced no tab items; aborting restore.");
                    }

                    if (immediate.OriginalIndex >= 0 && immediate.OriginalIndex < tabs.Items.Count)
                    {
                        tabs.SelectedIndex = immediate.OriginalIndex;
                    }
                });
            }
            catch (InvalidOperationException ex)
            {
                TerminalLogger.Log($"TryRestoreStartupSession: aborted ({ex.Message})");
                return false;
            }

            _startup.Checkpoint("StartupRestore.AfterTabMaterialization");

            InitializeRestoredTabs(tabs);
            _startup.Checkpoint("StartupRestore.AfterInitializeRestoredTabs");

            return true;
        }
```

The narrow `InvalidOperationException` catch matches only our intentional throw; any other exception (e.g. from `SessionManager.CreateRestoredTabItem`) keeps propagating as today.

- [ ] **Step 5: Delete `RunDeferredStartupRestore` and `HydrateDeferredStartupTab`-call-from-RunDeferred**

Find and **delete the entire** `RunDeferredStartupRestore` method (around lines 1280–1294):

```csharp
        private void RunDeferredStartupRestore()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            var plan = _pendingStartupRestorePlan;
            if (tabs == null || plan == null)
            {
                return;
            }

            _pendingStartupRestorePlan = null;
            _startupRestoreCoordinator.RunDeferred(
                plan.DeferredTabs,
                deferredTab => HydrateDeferredStartupTab(tabs, deferredTab),
                () => _startup.Mark(StartupPhase.BackgroundRestoreComplete));
        }
```

Leave `HydrateDeferredStartupTab` in place — it's still used by the inline lambda in Step 6.

- [ ] **Step 6: Replace the deferred-restore trigger in `RegisterPaneOwners` / startup-tabs region**

Around line 2164, replace:

```csharp
            if (_pendingStartupRestorePlan != null)
            {
                Dispatcher.UIThread.Post(RunDeferredStartupRestore, DispatcherPriority.Background);
            }
```

with:

```csharp
            if (_startup.HasPendingDeferredRestore)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var tabsControl = this.FindControl<TabControl>("Tabs");
                    if (tabsControl == null)
                    {
                        return;
                    }
                    _startup.DrainDeferred(deferredTab => HydrateDeferredStartupTab(tabsControl, deferredTab));
                }, DispatcherPriority.Background);
            }
```

This preserves the exact behavior of the old `RunDeferredStartupRestore`: do nothing if the `Tabs` control is missing, otherwise hand each deferred tab to `HydrateDeferredStartupTab`. `BackgroundRestoreComplete` is marked by the orchestrator via its `onCompleted` callback inside `DrainDeferred`.

- [ ] **Step 7: Consolidate the duplicated fresh-start path**

Around lines 2148–2162, the current code is:

```csharp
            if (tabs != null)
            {
                if (!TryRestoreStartupSession(tabs))
                {
                    AddTab(defaultProfile);
                    _startup.Mark(StartupPhase.SessionRestoreComplete);
                    _startup.Mark(StartupPhase.BackgroundRestoreComplete);
                }
            }
            else
            {
                AddTab(defaultProfile);
                _startup.Mark(StartupPhase.SessionRestoreComplete);
                _startup.Mark(StartupPhase.BackgroundRestoreComplete);
            }
```

(The `_startup.Mark(...)` substitutions were applied in Task 4.) Replace with:

```csharp
            if (tabs != null)
            {
                if (!TryRestoreStartupSession(tabs))
                {
                    AddTab(defaultProfile);
                    _startup.CompleteWithoutRestore();
                }
            }
            else
            {
                AddTab(defaultProfile);
                _startup.CompleteWithoutRestore();
            }
```

This is the one quiet correctness improvement the spec promised: collapses the duplicated mark-pair into a single `CompleteWithoutRestore()` call in each branch. Same observable behavior; the orchestrator owns the invariant.

- [ ] **Step 8: Build to confirm there are no dangling references**

Run:

```powershell
scripts/build.ps1 build src/NovaTerminal.App/NovaTerminal.App.csproj -c Release --no-restore
```

Expected: clean build. If you get `CS0103: The name '_pendingStartupRestorePlan' does not exist`, you missed a call site — search MainWindow.axaml.cs for `_pendingStartupRestorePlan` and `_startupRestoreCoordinator` and remove or migrate every remaining reference.

Run via Grep tool to verify zero remaining references:

```
pattern: _pendingStartupRestorePlan|_startupRestoreCoordinator|RunDeferredStartupRestore
path:    src/NovaTerminal.App/MainWindow.axaml.cs
output_mode: content
```

Expected: zero results.

- [ ] **Step 9: Run the regression test set**

Run:

```powershell
scripts/build.ps1 test -c Release --filter "FullyQualifiedName~MainWindowStartupTests|FullyQualifiedName~StartupOrchestratorTests|FullyQualifiedName~AppServicesTests|FullyQualifiedName~StartupPerformanceTrackerTests|FullyQualifiedName~StartupMetricsWriterTests|FullyQualifiedName~StartupMetricsSummaryTests|FullyQualifiedName~StartupRestoreCoordinatorTests|FullyQualifiedName~StartupRestorePlanTests|FullyQualifiedName~TerminalPaneStartupInstrumentationTests|FullyQualifiedName~SessionManagerTests" --no-restore
```

Expected: all pass.

- [ ] **Step 10: Commit**

```powershell
git commit src/NovaTerminal.App/MainWindow.axaml.cs -m "refactor: route MainWindow session restore through StartupOrchestrator"
```

---

### Task 6: Full-suite regression + measurement-gate documentation

**Files:**
- Modify: `docs/superpowers/specs/2026-05-26-startup-orchestrator-extraction-design.md` (mark status complete)

- [ ] **Step 1: Run the full test suite using the documented filter**

The README's documented filter excludes Replay, RenderMetrics, and PtySmoke categories:

```powershell
scripts/build.ps1 test -c Release --filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke" --no-restore
```

Expected: all pass. If any test outside the touched namespaces fails, investigate — but the suite has pre-existing flakes; verify the failure reproduces on `main` before blaming this PR.

- [ ] **Step 2: Capture a measurement baseline against `main`**

The spec's measurement gate requires before/after numbers. From a fresh terminal:

```powershell
# Stash any uncommitted work first (icon/PowerShell staging from the session start)
git stash push --keep-index -m "extraction-plan-baseline-stash"
git switch -d origin/main
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\tools\measure_startup.ps1 -Configuration Release -Label pre-orchestrator-extraction -Iterations 10
git switch -                                                  # back to the working branch
git stash pop                                                  # restore staging area
```

If `git stash push --keep-index` is unavailable for any reason (e.g. the staged work was previously stashed and popped), skip the stash step and capture the baseline from a clean checkout of `origin/main` in a separate worktree:

```powershell
git worktree add ../nova2-baseline origin/main
cd ../nova2-baseline
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\tools\measure_startup.ps1 -Configuration Release -Label pre-orchestrator-extraction -Iterations 10
cd -
git worktree remove ../nova2-baseline
```

- [ ] **Step 3: Capture the post-change measurement**

From the working branch with this plan's commits applied:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\tools\measure_startup.ps1 -Configuration Release -Label post-orchestrator-extraction -Iterations 10
```

- [ ] **Step 4: Generate the comparison report**

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\tools\summarize_startup_metrics.ps1 pre-orchestrator-extraction post-orchestrator-extraction
```

Expected: deltas on `Main Window Constructed`, `Window Opened`, `First Terminal Ready`, `Session Restore Complete` are within ±2%. Save the output text — it goes in the PR description.

If any delta is outside ±2%, investigate. Likely causes (in priority order):
1. A migrated checkpoint name was typo'd — re-check Task 4's substitution table against the output JSONL.
2. The orchestrator is being constructed too early or too late — verify the order in `App.OnFrameworkInitializationCompleted` exactly matches the spec.
3. Genuine background restore drift — if `BackgroundRestoreComplete` regressed >2%, check that the per-tab try/catch wrapper in `DrainDeferred` isn't catching legitimate exceptions that the old code let propagate.

- [ ] **Step 5: Mark the spec as complete**

In `docs/superpowers/specs/2026-05-26-startup-orchestrator-extraction-design.md`, change the second header line from:

```
**Status:** Spec — awaiting user review before writing implementation plan
```

to:

```
**Status:** Implemented (see docs/superpowers/plans/2026-05-26-startup-orchestrator-extraction-plan.md)
```

- [ ] **Step 6: Commit the doc update**

```powershell
git commit docs/superpowers/specs/2026-05-26-startup-orchestrator-extraction-design.md -m "docs: mark startup-orchestrator extraction spec as implemented"
```

- [ ] **Step 7: Open the PR**

```powershell
gh pr create --title "refactor: extract StartupOrchestrator from MainWindow" --body "$(cat <<'EOF'
## Summary
- Extract `StartupOrchestrator` from `MainWindow.axaml.cs` and introduce the `AppServiceBundle` composition-root pattern.
- Migrate 20+ scattered `StartupPerformanceTracker.Current?.TryMark*` call sites to the orchestrator.
- Move session-restore lifecycle (`_pendingStartupRestorePlan`, `RunDeferredStartupRestore`, `_startupRestoreCoordinator`) behind the orchestrator.
- Collapse a duplicated `Mark(SessionRestoreComplete) + Mark(BackgroundRestoreComplete)` pair into a single `CompleteWithoutRestore()` call.

## Why
PR #67 added the instrumentation building blocks but left the orchestration inline in MainWindow (now 5249 lines). This extraction removes the global-static `StartupPerformanceTracker.Current?.` antipattern from MainWindow, makes the startup state machine independently unit-testable, and establishes the `AppServiceBundle` pattern that future MainWindow-cluster extractions (TabRuntimeRegistry, PaneZoomController, etc.) will follow.

## Out of scope
- TerminalPane settings threading (P4-#15 from the review doc)
- Background Restore +5.71% regression recovery from PR #67
- DI container adoption
- Any new phases, checkpoints, or measurements

## Measurement
[Paste the output from `summarize_startup_metrics.ps1 pre-orchestrator-extraction post-orchestrator-extraction` here. All deltas within ±2%.]

## Test plan
- [x] `scripts/build.ps1 test -c Release --filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke"`
- [x] `scripts/build.ps1 build src/NovaTerminal.App/NovaTerminal.App.csproj -c Release`
- [x] Measurement gate within ±2%

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

### Notes for execution

- **Staged-work safety:** the session that produced this plan left the icon-ico-swap + PowerShell `-File` unquote fix in the staging area. Every `git commit` in this plan uses explicit paths, so those changes are never swept into a commit produced by this plan. If a commit step ever lands those files, stop immediately and run `git reset --soft HEAD~1` then re-commit with explicit paths.
- **Build wrappers are mandatory:** raw `dotnet build` / `dotnet test` hangs the Bash tool's pipe per `CLAUDE.md`. Always use `scripts/build.ps1` / `scripts/build.sh`. If a test step appears stuck, kill it and re-run via the wrapper.
- **Do NOT touch:** `NovaTerminal.VT`, `NovaTerminal.Rendering`, `NovaTerminal.Replay`, `TerminalPane.axaml.cs`'s `FirstTerminalReady` mark, the `StartupPerformanceTracker.Current` static accessor (it must continue to work for TerminalPane), the `StartupRestoreCoordinator` and `StartupRestorePlan` source files.
- **Commit granularity:** each task produces exactly one commit. Six commits total. If a task step fails partway, fix forward on the same commit (do not split). If you realize a previous task's commit needs a fix, add a follow-up commit rather than amending.
- **Existing in-repo stashes** (`stash@{0..5}`): leave them alone. None are related to this work.

### Recommended execution order

1. Task 1 — StartupOrchestrator + tests
2. Task 2 — AppServiceBundle + AppServices + tests
3. Task 3 — Wire MainWindow through the bundle (no call-site migration)
4. Task 4 — Migrate Mark/Checkpoint call sites
5. Task 5 — Migrate session-restore lifecycle + consolidate fresh-start
6. Task 6 — Regression + measurement gate + PR
