using System;
using System.Collections.Generic;

namespace NovaTerminal.Core;

public sealed class StartupRestoreCoordinator
{
    private readonly Action<Action> _schedule;

    public StartupRestoreCoordinator(Action<Action> schedule)
    {
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    }

    public void RunDeferred(
        IReadOnlyList<StartupRestoreTab> deferredTabs,
        Action<StartupRestoreTab> restoreTab,
        Action onCompleted)
    {
        ArgumentNullException.ThrowIfNull(deferredTabs);
        ArgumentNullException.ThrowIfNull(restoreTab);
        ArgumentNullException.ThrowIfNull(onCompleted);

        if (deferredTabs.Count == 0)
        {
            onCompleted();
            return;
        }

        _schedule(() =>
        {
            foreach (var deferredTab in deferredTabs)
            {
                restoreTab(deferredTab);
            }
            onCompleted();
        });
    }
}
