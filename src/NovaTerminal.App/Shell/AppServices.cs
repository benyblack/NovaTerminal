using System;

namespace NovaTerminal.Shell;

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
        return new AppServiceBundle(orchestrator, CommandAssistServices.CreateDefault());
    }

    public static AppServiceBundle BuildForDesigner()
    {
        // Prefer the global tracker if Program.Main has set it (production startup,
        // or a test that called StartupPerformanceTracker.StartNewCurrent). This
        // keeps the orchestrator's tracker in sync with legacy callers that still
        // use StartupPerformanceTracker.Current (TerminalPane, SessionManager).
        // Fall back to a fresh tracker only for true designer-mode XAML preview
        // where Program.Main has never run.
        var tracker = StartupPerformanceTracker.Current ?? new StartupPerformanceTracker();
        var coordinator = new StartupRestoreCoordinator(action => action());
        var orchestrator = new StartupOrchestrator(tracker, coordinator);
        // Fresh default settings, never TerminalSettings.Load(): designer previews and the tests
        // that build windows through this path must not depend on whatever settings.json the
        // developer's machine happens to carry (the #357 settings-bleed pathology).
        return new AppServiceBundle(orchestrator, CommandAssistServices.CreateDefault(), new TerminalSettings());
    }
}
