using System;
using System.Collections.Generic;
using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests.Core;

public sealed class StartupOrchestratorTests
{
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
        var (tracker, _) = TestTrackerFactory.CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());

        orchestrator.Mark(StartupPhase.WindowOpened);

        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.WindowOpened, out _));
    }

    [Fact]
    public void Checkpoint_DelegatesToTracker()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());

        orchestrator.Checkpoint("MainWindow.AfterApplyTheme");

        var snapshot = tracker.CreateSnapshot();
        Assert.NotNull(snapshot.Checkpoints);
        Assert.True(snapshot.Checkpoints!.ContainsKey("MainWindow.AfterApplyTheme"));
    }

    [Fact]
    public void Constructor_ThrowsOnNullDependencies()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
        var coordinator = CreateInlineCoordinator();

        Assert.Throws<ArgumentNullException>(() => new StartupOrchestrator(null!, coordinator));
        Assert.Throws<ArgumentNullException>(() => new StartupOrchestrator(tracker, null!));
    }

    [Fact]
    public void BeginSessionRestore_ThrowsOnNullArguments()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var session = SessionWith(tabCount: 1, activeIndex: 0);

        Assert.Throws<ArgumentNullException>(() => orchestrator.BeginSessionRestore(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => orchestrator.BeginSessionRestore(session, null!));
    }

    [Fact]
    public void DrainDeferred_ThrowsOnNullArgument()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());

        Assert.Throws<ArgumentNullException>(() => orchestrator.DrainDeferred(null!));
    }

    [Fact]
    public void BeginSessionRestore_WithDeferredTabs_CallsMaterializeImmediateOnce_AndStashesPlan()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
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
        var (tracker, _) = TestTrackerFactory.CreateTracker();
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
    public void BeginSessionRestore_WhenMaterializeThrows_PropagatesAndLeavesStateUnchanged()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
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
    public void BeginSessionRestore_CalledTwiceWithDeferredPending_Throws()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var session = SessionWith(tabCount: 3, activeIndex: 1);

        orchestrator.BeginSessionRestore(session, _ => { });
        Assert.True(orchestrator.HasPendingDeferredRestore);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            orchestrator.BeginSessionRestore(session, _ => { }));
        Assert.Contains("already called", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteWithoutRestore_MarksBothPhases()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());

        orchestrator.CompleteWithoutRestore();

        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.SessionRestoreComplete, out _));
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
    }

    [Fact]
    public void CompleteWithoutRestore_IsIdempotent()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());

        orchestrator.CompleteWithoutRestore();
        orchestrator.CompleteWithoutRestore();

        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.SessionRestoreComplete, out _));
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
    }

    [Fact]
    public void DrainDeferred_WithPendingPlan_RunsAllDeferredTabsInOriginalOrder_AndMarksBackground()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
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
        var (tracker, _) = TestTrackerFactory.CreateTracker();
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
        var (tracker, _) = TestTrackerFactory.CreateTracker();
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

    [Fact]
    public void DrainDeferred_WithCapturingScheduler_DoesNotMaterializeUntilSchedulerFires()
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
        var captured = new List<Action>();
        var coordinator = CreateCapturingCoordinator(captured);
        var orchestrator = new StartupOrchestrator(tracker, coordinator);
        var session = SessionWith(tabCount: 3, activeIndex: 0);
        orchestrator.BeginSessionRestore(session, _ => { });
        var hydrated = new List<int>();

        orchestrator.DrainDeferred(tab => hydrated.Add(tab.OriginalIndex));

        // Before the scheduler fires the captured action, nothing should have run.
        Assert.Empty(hydrated);
        Assert.False(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
        // _pendingPlan must already be cleared so a second DrainDeferred call is a no-op.
        Assert.False(orchestrator.HasPendingDeferredRestore);

        // Fire the captured action — now the deferred loop and onCompleted should run.
        Assert.Single(captured);
        captured[0]();

        Assert.Equal(new[] { 1, 2 }, hydrated);
        Assert.True(tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(5, 3)]
    public void Invariant_AfterBeginSessionRestore_BackgroundCompleteImpliesNoPendingPlan(int tabCount, int activeIndex)
    {
        var (tracker, _) = TestTrackerFactory.CreateTracker();
        var orchestrator = new StartupOrchestrator(tracker, CreateInlineCoordinator());
        var session = SessionWith(tabCount, activeIndex);

        orchestrator.BeginSessionRestore(session, _ => { });

        bool backgroundComplete = tracker.TryGetElapsedMilliseconds(StartupPhase.BackgroundRestoreComplete, out _);
        Assert.Equal(backgroundComplete, !orchestrator.HasPendingDeferredRestore);
    }
}
