using System;

namespace NovaTerminal.Core;

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
