using System;
using NovaTerminal.VT;
using NovaTerminal.Pty;

namespace NovaTerminal.Core;

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

        if (_pendingPlan is not null)
        {
            throw new InvalidOperationException(
                "BeginSessionRestore was already called; the previous plan must be drained via DrainDeferred before starting another restore.");
        }

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
