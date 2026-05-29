# StartupOrchestrator Extraction Design

**Date:** 2026-05-26
**Status:** Implemented (see docs/superpowers/plans/2026-05-26-startup-orchestrator-extraction-plan.md, branch refactor/startup-orchestrator)
**Scope:** App layer only. Pure refactor; sets composition-root pattern for future MainWindow-cluster extractions.
**Owner:** N/A (single-author repo)

## Context

PR #67 ([codex] Improve startup instrumentation and critical path) added
`StartupPerformanceTracker`, `StartupRestoreCoordinator`, `StartupRestorePlan`,
`StartupMetricsWriter`, `StartupMetricsAnalysis` and a measurement harness,
deferred window icon decoding, and produced 8–12% wins on the four primary
startup checkpoints (`Main Window Constructed`, `Window Opened`,
`First Terminal Ready`, `Session Restore Complete`).

The building blocks now exist, but the orchestration sits inline inside
`MainWindow.axaml.cs` (now 5 249 lines). Specifically:

- 20+ scattered `StartupPerformanceTracker.Current?.TryMark*(...)` call sites
  (MainWindow lines 117, 1217, 1219, 1234, 1247, 1248, 1252, 1283, 1289, 1293,
  1967, 1969, 1979, 1987, 1996, 1997, 2100, 2103, 2153, 2154, 2160, 2161, 2168,
  2347, 2585).
- `_startupRestoreCoordinator` field constructed in the ctor (line 1973).
- `_pendingStartupRestorePlan` field (line 88) plus its lifecycle.
- A duplicated `Mark(SessionRestoreComplete) + Mark(BackgroundRestoreComplete)`
  pair at lines 2153–2154 and 2160–2161 (the "no restore happened" branches).
- A private `RunDeferredStartupRestore()` method that talks to the coordinator.

The next ~10% of startup wins (constructor diet, further phase deferral) is
gated on having an explicit orchestration surface. This design extracts that
surface and establishes the composition-root pattern (`AppServiceBundle`) that
all future MainWindow-cluster extractions (`TabRuntimeRegistry`,
`PaneZoomController`, `RecordingToastController`,
`TransferOverlayDragController`) will follow.

The companion review document
(`C:\Users\behna\.claude\plans\review-the-project-deeply-generic-cake.md`)
ranks this as the top remaining structural item under P0-#1.

## Goal

Extract `StartupOrchestrator` from `MainWindow` and introduce
`AppServiceBundle` as the composition-root pattern, with:

- zero net behavior change (pure refactor; one quiet correctness improvement
  noted below)
- the orchestrator unit-testable without Avalonia bootstrap
- the wiring pattern (`AppServices.Build(...) → AppServiceBundle → MainWindow`)
  established so subsequent extractions add one record field, not a ctor
  parameter

## Non-goals

- No new startup phases, checkpoints, or metrics surfaces. This PR moves
  existing calls, it does not change what is measured.
- No `IStartupTracker` interface or `StartupCheckpoints` string-constants
  module. These were the "Wide" option in brainstorming; both are deferred.
- No TerminalPane migration. TerminalPane keeps
  `StartupPerformanceTracker.Current?.TryMark(StartupPhase.FirstTerminalReady)`
  as-is. The static `Current` accessor remains.
- No DI container (`Microsoft.Extensions.DependencyInjection`) adoption.
- No fix to the `TerminalSettings.Load()` per-pane reload (P4-#15) and no
  attempt to claw back PR #67's +5.71% `Background Restore Complete`
  regression. Both are separate follow-up PRs.
- No changes to `NovaTerminal.VT`, `NovaTerminal.Rendering`, or
  `NovaTerminal.Replay`.

## Constraints

- Native AOT (`PublishAot=true`): no reflection-based DI, no open generics,
  no assembly scanning.
- Per `AGENTS.md`: additive changes preferred; keep interfaces small and
  explicit.
- The XAML compiler instantiates `MainWindow` via its parameterless ctor at
  design time. A parameterless ctor must remain on `MainWindow`.
- All new code must be deterministically unit-testable without spinning up
  Avalonia.

## Recommended approach

Three decisions, taken in brainstorming:

1. **Wiring pattern** — `static AppServices.Build(...)` returns a
   `record AppServiceBundle` that `MainWindow`'s ctor accepts as its sole
   parameter. Future services add a record field; ctor signature does not
   grow. (Chosen over direct ctor injection and over MS.E.DependencyInjection.)
2. **Orchestrator scope** — Medium: phases, checkpoints, session-restore
   lifecycle, pending-plan field, fresh-start path consolidation.
   (Chosen over Minimal "state holder" and Wide "facade + constants".)
3. **Primary refactor goal** — Open the door to a bigger refactor (set the
   pattern). The orchestrator is the prototype that future cluster
   extractions copy.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│ Program.cs                                                       │
│   StartupPerformanceTracker.StartNewCurrent()  // unchanged      │
│   AppBuilder.Configure<App>()...StartWithClassicDesktop()        │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│ App.axaml.cs                                                     │
│   var tracker  = StartupPerformanceTracker.Current!              │
│   var services = AppServices.Build(                              │
│       tracker,                                                   │
│       schedule: action => Dispatcher.UIThread.Post(              │
│           action, DispatcherPriority.Background))                │
│   desktop.MainWindow = new MainWindow(services)                  │
│   services.Startup.Mark(StartupPhase.MainWindowConstructed)      │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│ AppServiceBundle (record)                                        │
│   StartupOrchestrator Startup                                    │
│   // future PRs: TabRuntimeRegistry, PaneZoomController, ...     │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│ StartupOrchestrator                                              │
│   wraps  StartupPerformanceTracker     (ctor-injected)           │
│   owns   StartupRestoreCoordinator     (ctor-injected)           │
│   owns   StartupRestorePlan? _pendingPlan                        │
│                                                                  │
│   Mark / Checkpoint                                              │
│   BeginSessionRestore(session, materializeImmediate)             │
│       → returns StartupRestoreOutcome                            │
│   DrainDeferred(materializeTab)                                  │
│   CompleteWithoutRestore()                                       │
│   HasPendingDeferredRestore : bool                               │
└─────────────────────────────────────────────────────────────────┘
                                ▲
                                │ holds reference to
┌─────────────────────────────────────────────────────────────────┐
│ MainWindow                                                       │
│   private readonly StartupOrchestrator _startup;                 │
│   public MainWindow(AppServiceBundle services) { ... }           │
│   public MainWindow() : this(AppServices.BuildForDesigner()) {}  │
└─────────────────────────────────────────────────────────────────┘
```

**Key boundaries**

- `StartupPerformanceTracker` is unchanged. The orchestrator composes it; it
  does not replace it. The static `Current` accessor remains for legacy
  call sites (TerminalPane, the design-time bundle).
- `StartupRestoreCoordinator` and `StartupRestorePlan` are unchanged. The
  orchestrator instantiates and owns the coordinator.
- `App.axaml.cs:20`'s `Mark(MainWindowConstructed)` stays in App (the moment
  is "ctor returned"). Its target becomes `services.Startup` instead of the
  static.

## Components

### `StartupOrchestrator` (new — `src/NovaTerminal.App/Core/StartupOrchestrator.cs`)

```csharp
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
        _tracker     = tracker     ?? throw new ArgumentNullException(nameof(tracker));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public void Mark(StartupPhase phase) => _tracker.TryMark(phase);

    public void Checkpoint(string name) => _tracker.TryMarkCheckpoint(name);

    public void BeginSessionRestore(
        NovaSession session,
        Action<StartupRestoreTab> materializeImmediate);

    public void DrainDeferred(Action<StartupRestoreTab> materializeTab);

    public void CompleteWithoutRestore();

    public bool HasPendingDeferredRestore => _pendingPlan is not null;
}
```

Behavior:

- `BeginSessionRestore` calls `StartupRestorePlan.Create(session)`, invokes
  `materializeImmediate` synchronously with the plan's immediate tab, then
  marks `SessionRestoreComplete`. If `plan.DeferredTabs.Count == 0`, also
  marks `BackgroundRestoreComplete` and leaves `_pendingPlan` null. Else
  stashes `_pendingPlan = plan`. Returns `void` — callers query
  `HasPendingDeferredRestore` (which is also queried later, after the
  no-restore branch, so a single property is the source of truth).
- `DrainDeferred` no-ops if `_pendingPlan` is null. Otherwise clears
  `_pendingPlan` and calls `_coordinator.RunDeferred(plan.DeferredTabs,
  perTab, onCompleted: () => _tracker.TryMark(BackgroundRestoreComplete))`,
  where `perTab` is a local function defined inside `DrainDeferred` that
  wraps the caller's `materializeTab` in `try { materializeTab(tab); }
  catch (Exception ex) { TerminalLogger.Log(ex); }`. The coordinator itself
  is unchanged; the try/catch lives in the orchestrator's lambda, not in
  `StartupRestoreCoordinator`. This isolates one bad tab from the rest of
  the loop and from `BackgroundRestoreComplete` firing.
- `CompleteWithoutRestore` marks both `SessionRestoreComplete` and
  `BackgroundRestoreComplete`. Idempotent.

### `AppServiceBundle` (new — `src/NovaTerminal.App/Core/AppServiceBundle.cs`)

```csharp
namespace NovaTerminal.Platform;

public sealed record AppServiceBundle(StartupOrchestrator Startup);
```

Adding a future service is one record field:

```csharp
public sealed record AppServiceBundle(
    StartupOrchestrator Startup,
    TabRuntimeRegistry  Tabs);   // ← future PR adds this line
```

### `AppServices` (new — `src/NovaTerminal.App/Core/AppServices.cs`)

```csharp
namespace NovaTerminal.Platform;

public static class AppServices
{
    public static AppServiceBundle Build(
        StartupPerformanceTracker tracker,
        Action<Action> schedule)
    {
        var coordinator  = new StartupRestoreCoordinator(schedule);
        var orchestrator = new StartupOrchestrator(tracker, coordinator);
        return new AppServiceBundle(orchestrator);
    }

    public static AppServiceBundle BuildForDesigner()
    {
        var tracker      = new StartupPerformanceTracker();
        var coordinator  = new StartupRestoreCoordinator(action => action());
        var orchestrator = new StartupOrchestrator(tracker, coordinator);
        return new AppServiceBundle(orchestrator);
    }
}
```

`BuildForDesigner` exists solely to satisfy the parameterless `MainWindow`
ctor that the XAML designer requires. Production code calls `Build`.

### `MainWindow` ctor

```csharp
private readonly StartupOrchestrator _startup;

public MainWindow(AppServiceBundle services)
{
    _startup = services.Startup;
    // ... existing ctor body, with every
    //     StartupPerformanceTracker.Current?.TryMark*(...) replaced by
    //     _startup.Mark(...) / _startup.Checkpoint(...)
}

public MainWindow() : this(AppServices.BuildForDesigner()) { }
```

### `App.axaml.cs`

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
            schedule: action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));

        var mainWindow = new MainWindow(services);
        desktop.MainWindow = mainWindow;

        services.Startup.Mark(StartupPhase.MainWindowConstructed);
    }
    base.OnFrameworkInitializationCompleted();
}
```

## Data flow

### Startup walkthrough (after the extraction)

```
TIME    LAYER         CALL                                                EFFECT
────    ─────         ────                                                ──────
t=0     Program       StartupPerformanceTracker.StartNewCurrent()         tracker.Current set; t0 captured
        Program       AppBuilder...StartWithClassicDesktop()              Avalonia starts

        App           tracker  = StartupPerformanceTracker.Current!
                      services = AppServices.Build(tracker, schedule)     orchestrator constructed
                      w        = new MainWindow(services)                 ctor runs (see below)
                      services.Startup.Mark(MainWindowConstructed)        phase fires (was App.axaml.cs:20)
                      desktop.MainWindow = w

══════ MainWindow ctor (synchronous) ═════════════════════════════════════════════════════════════
        MainWindow    _startup = services.Startup
                      InitializeComponent()
                      _startup.Checkpoint("MainWindow.AfterInitializeComponent")
                      _settings = TerminalSettings.Load()
                      _startup.Checkpoint("MainWindow.AfterSettingsLoad")
                      // StartupRestoreCoordinator instantiation REMOVED here —
                      // owned by orchestrator via AppServices.Build
                      _startup.Checkpoint("MainWindow.AfterLegacyMigration")
                      ApplyTheme()
                      _startup.Checkpoint("MainWindow.AfterApplyTheme")

                      // Session restore branch:
                      if (session != null && session.Tabs.Count > 0) {
                          _startup.Checkpoint("StartupRestore.AfterSessionLoad")
                          _startup.BeginSessionRestore(
                              session,
                              immediate => MaterializeTabUi(immediate))
                          // orchestrator: creates plan; calls callback ONCE
                          // for immediate; marks SessionRestoreComplete;
                          // if deferred.Count == 0 also marks
                          // BackgroundRestoreComplete and leaves _pendingPlan
                          // null; else stashes _pendingPlan
                          _startup.Checkpoint("StartupRestore.AfterTabMaterialization")
                          InitializeRestoredTabsUi()
                          _startup.Checkpoint("StartupRestore.AfterInitializeRestoredTabs")
                      } else {
                          AddTab(defaultProfile)
                          _startup.CompleteWithoutRestore()
                          // replaces duplicated pair at MainWindow:2153-2154 and :2160-2161
                      }

                      if (_startup.HasPendingDeferredRestore) {
                          Dispatcher.UIThread.Post(
                              () => _startup.DrainDeferred(t => MaterializeTabUi(t)),
                              DispatcherPriority.Background)
                      }
                      _startup.Checkpoint("MainWindow.AfterInitialTabs")
                      _startup.Checkpoint("MainWindow.AfterCoreUiWireup")
                      _startup.Checkpoint("MainWindow.CtorComplete")
══════════════════════════════════════════════════════════════════════════════════════════════════

t≈X     MainWindow.OnOpened          _startup.Mark(WindowOpened)
                                                                          (was MainWindow.axaml.cs:117)

t≈Y     TerminalPane (first)         StartupPerformanceTracker.Current?.TryMark(
                                         FirstTerminalReady)
                                                                          unchanged — pane keeps the
                                                                          static accessor

t≈Z     MainWindow Loaded            _startup.Checkpoint("MainWindow.LoadedPostStart")
                                     // ... wait for UI ready ...
                                     _startup.Checkpoint("MainWindow.LoadedPostUiReady")
                                     _startup.Mark(DeferredWorkComplete)

t≈Z+    Dispatcher (Background)      _startup.DrainDeferred(t => MaterializeTabUi(t))
                                     // orchestrator: if _pendingPlan, calls
                                     // coordinator.RunDeferred which posts the loop;
                                     // on completion marks BackgroundRestoreComplete;
                                     // snapshot writer fires.
```

### Call-site migration map

| Today (`MainWindow.axaml.cs`) | After |
|---|---|
| `_startupRestoreCoordinator = new StartupRestoreCoordinator(...)` (line 1973) | deleted — orchestrator owns it (constructed inside `AppServices.Build`) |
| `_pendingStartupRestorePlan` field (line 88) | deleted — orchestrator owns it |
| `StartupPerformanceTracker.Current?.TryMark*(...)` × 20+ sites | `_startup.Mark(...)` or `_startup.Checkpoint(...)` |
| `var plan = StartupRestorePlan.Create(session)` + immediate materialization + marks (lines 1219–1257) | `_startup.BeginSessionRestore(session, immediate => MaterializeTabUi(immediate))` |
| Private `RunDeferredStartupRestore()` method (around line 1280) | deleted — replaced by `_startup.DrainDeferred(t => MaterializeTabUi(t))` |
| Duplicated `TryMark(SessionRestoreComplete) + TryMark(BackgroundRestoreComplete)` at lines 2153–2154 **and** 2160–2161 | single `_startup.CompleteWithoutRestore()` in each branch |
| `if (_pendingStartupRestorePlan != null) { Post(RunDeferredStartupRestore) }` (line 2164) | `if (_startup.HasPendingDeferredRestore) { Post(() => _startup.DrainDeferred(...)) }` |

### Threading

- `BeginSessionRestore` runs on the UI thread (called from MainWindow ctor).
  The `materializeImmediate` callback is invoked synchronously on the same
  thread.
- `DrainDeferred` is expected on the UI thread. It calls into the coordinator,
  which uses the injected `schedule` (`Dispatcher.UIThread.Post` in
  production) to run the deferred loop later — same threading as today.
- No new locks. `_pendingPlan` is touched only on the UI thread.

### Net observable behavior

Zero, with one quiet correctness improvement: the duplicated
`Mark(SessionRestoreComplete) + Mark(BackgroundRestoreComplete)` pair at
MainWindow:2153–2154 and :2160–2161 collapses into a single
`CompleteWithoutRestore()` call. Same metrics emit, smaller risk surface.

## Error handling

The orchestrator inherits the design doc's error policy: startup must remain
resilient; instrumentation must not hang the app.

| Failure | Behavior |
|---|---|
| `tracker` or `coordinator` null in orchestrator ctor | `ArgumentNullException` — fail fast, wiring bug |
| `BeginSessionRestore(session: null, ...)` | `ArgumentNullException` |
| `BeginSessionRestore` with empty `session.Tabs` | propagates `ArgumentException` from `StartupRestorePlan.Create` (existing behavior) — caller is expected to call `CompleteWithoutRestore` instead |
| `materializeImmediate` callback throws inside `BeginSessionRestore` | exception propagates; orchestrator state unchanged (phases NOT marked, `_pendingPlan` NOT set) so MainWindow's existing try/catch can call `CompleteWithoutRestore` to keep startup metrics complete |
| `materializeTab` callback throws inside `DrainDeferred` | wrapped in `try/catch` per-tab; failing tab is logged via `TerminalLogger.Log`; remaining tabs still materialize; `BackgroundRestoreComplete` still marked |
| `Mark` / `Checkpoint` called twice | silently no-op via underlying `TryMark` / `TryMarkCheckpoint` |
| `DrainDeferred` with no pending plan | no-op |
| `CompleteWithoutRestore` after `BeginSessionRestore` already marked phases | no-op (idempotent via tracker) |
| `StartupMetricsWriter` failure | existing tracker behavior (silent retry on next mark) — orchestrator does not intercept |
| App exits before `DrainDeferred` runs | pending plan dropped, snapshot not written for this launch (acceptable per design doc) |

### Invariant enforced by the orchestrator

> After `BeginSessionRestore` returns, exactly one of:
> - `HasPendingDeferredRestore == true` and `BackgroundRestoreComplete` is **not** marked, or
> - `HasPendingDeferredRestore == false` and `BackgroundRestoreComplete` **is** marked.

This is the invariant today's duplicated MainWindow code can violate by
accident in the "session exists but restore fails" path. Asserted by unit
test.

## Testing strategy

All new tests in `tests/NovaTerminal.Tests/Core/`. The orchestrator is a pure
C# class with no Avalonia dependency — tests run in xUnit, no headless
bootstrap.

### `StartupOrchestratorTests` (new file)

Fixture: real `StartupPerformanceTracker` constructed via the existing
internal test ctor for controllable time; `StartupRestoreCoordinator` with a
capturing fake schedule (`Action<Action>` that stores the deferred action so
the test can run it on demand). A small `IStartupLogProbe` (or
`TerminalLogger` test seam, whichever is least invasive) captures any logged
exceptions for assertion.

| Test | Asserts |
|---|---|
| `Mark_DelegatesToTracker` | After `Mark(WindowOpened)`, `tracker.TryGetElapsedMilliseconds(WindowOpened, out _) == true` |
| `Checkpoint_DelegatesToTracker` | After `Checkpoint("X")`, `tracker.CreateSnapshot().Checkpoints["X"]` has a value |
| `BeginSessionRestore_WithDeferredTabs_CallsMaterializeImmediateOnce_AndStashesPlan` | 3-tab session, active=1 → callback invoked once with `OriginalIndex == 1`; `HasPendingDeferredRestore == true`; `SessionRestoreComplete` marked; `BackgroundRestoreComplete` NOT marked |
| `BeginSessionRestore_WithSingleTab_MarksBothPhasesAndClearsPending` | 1-tab session → callback invoked once; `HasPendingDeferredRestore == false`; both phases marked |
| `BeginSessionRestore_WhenMaterializeThrows_DoesNotMarkPhases` | callback throws → exception bubbles; `SessionRestoreCompleteMs == null`; `HasPendingDeferredRestore == false` |
| `CompleteWithoutRestore_MarksBothPhases` | both `SessionRestoreCompleteMs` and `BackgroundRestoreCompleteMs` populated |
| `CompleteWithoutRestore_IsIdempotent` | calling twice does not throw; snapshot stable |
| `DrainDeferred_WithPendingPlan_RunsAllDeferredTabsInOriginalOrder_AndMarksBackground` | 3 deferred tabs at indices [0, 2, 3] → callback invoked 3× in that exact order; `BackgroundRestoreCompleteMs` populated; `HasPendingDeferredRestore == false` after |
| `DrainDeferred_WithoutPendingPlan_IsNoOp` | fresh orchestrator → call does nothing; no phases marked |
| `DrainDeferred_WhenOneTabThrows_StillCompletesAndMarksBackground` | middle tab throws → other two still materialize; `BackgroundRestoreComplete` still marked; exception captured by log probe |
| `Invariant_AfterBeginSessionRestore_BackgroundCompleteImpliesNoPendingPlan` | across {1, 2, 5}-tab sessions, the Section "Invariant" property holds |

### `AppServicesTests` (new file)

| Test | Asserts |
|---|---|
| `Build_ReturnsBundleWithWiredOrchestrator` | `bundle.Startup` non-null; its `Mark` call advances the supplied tracker |
| `BuildForDesigner_ReturnsBundleWithNoOpScheduler` | designer bundle's `DrainDeferred` runs synchronously (verified by callback ordering against a probe list) |

### Existing tests — adjustment only

| File | Change |
|---|---|
| `tests/NovaTerminal.Tests/Core/MainWindowStartupTests.cs` | introduce a small test helper `TestMainWindowFactory.Create()` that returns `new MainWindow(AppServices.BuildForDesigner())`; every fixture call site of `new MainWindow()` uses the helper. No assertion changes. The helper lives in `tests/NovaTerminal.Tests/Core/TestMainWindowFactory.cs` so future MainWindow ctor changes touch one file. |
| `tests/NovaTerminal.Tests/Core/StartupRestoreCoordinatorTests.cs` | unchanged |
| `tests/NovaTerminal.Tests/Core/StartupRestorePlanTests.cs` | unchanged |
| `tests/NovaTerminal.Tests/Core/StartupPerformanceTrackerTests.cs` | unchanged |
| `tests/NovaTerminal.Tests/Core/TerminalPaneStartupInstrumentationTests.cs` | unchanged (TerminalPane still uses the static) |
| `tests/NovaTerminal.Tests/Core/TerminalPaneRemoteFilesSidebarTests.cs` | unchanged (no startup dependency) |

### Verification — measurement gate

Per PR #67's harness, before and after this PR:

```powershell
pwsh tests\tools\measure_startup.ps1 -Configuration Release -Label pre-orchestrator-extraction  -Iterations 10
# merge PR
pwsh tests\tools\measure_startup.ps1 -Configuration Release -Label post-orchestrator-extraction -Iterations 10
pwsh tests\tools\summarize_startup_metrics.ps1 pre-orchestrator-extraction post-orchestrator-extraction
```

Acceptance: deltas on `Main Window Constructed`, `Window Opened`,
`First Terminal Ready`, `Session Restore Complete` within ±2%. This is a
pure refactor; larger swings either direction warrant investigation.

## Alternatives considered

### B — Direct ctor injection (no bundle)

`MainWindow(StartupOrchestrator startup)`. Smaller today, but every future
extraction widens the ctor signature and every test fixture pays the cost.
Defeats the "open the door to a bigger refactor" goal. Rejected.

### C — Microsoft.Extensions.DependencyInjection (manual-registration, AOT-safe)

Industry-standard testability but requires a new NuGet dep (~1 MB to AOT
bundle), introduces a culture change a single-window desktop app may not need,
and carries ongoing AOT-corner-case vigilance (open generics, `IOptions<>`,
scanning). The composition-root record satisfies the same testability needs at
a fraction of the surface area. Rejected for this PR; revisit only if a
genuine multi-scope lifetime need appears.

### Wide orchestrator scope (`IStartupTracker` + `StartupCheckpoints`)

Pulls TerminalPane and all magic-string checkpoints into the orchestration
module. Larger PR, more files touched, no immediate behavior benefit. Deferred
to a follow-up if/when TerminalPane is refactored to receive services
explicitly.

### Minimal "state holder" orchestrator

Just exposes the tracker and coordinator as properties; MainWindow still
drives every detail. Fails to consolidate the duplicated `CompleteWithoutRestore`
path; barely reduces MainWindow's responsibility surface. Rejected.

## Out of scope (named explicitly to prevent scope creep)

- `IStartupTracker` interface
- `StartupCheckpoints` string-constants module
- TerminalPane settings threading (P4-#15)
- Background Restore +5.71% regression recovery
- DI container adoption
- Any change to `NovaTerminal.VT`, `NovaTerminal.Rendering`,
  `NovaTerminal.Replay`
- Further MainWindow cluster extractions (`TabRuntimeRegistry`,
  `PaneZoomController`, etc.) — those follow this pattern in separate PRs

## Open questions

None. All design decisions were resolved during brainstorming.
