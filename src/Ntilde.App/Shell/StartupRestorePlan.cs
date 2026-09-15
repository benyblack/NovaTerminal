using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.Pty;

namespace NovaTerminal.Shell;

public sealed record StartupRestoreTab(int OriginalIndex, TabSession Tab);

public sealed record StartupRestorePlan(StartupRestoreTab ImmediateTab, IReadOnlyList<StartupRestoreTab> DeferredTabs)
{
    public static StartupRestorePlan Create(NovaSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Tabs.Count == 0)
        {
            throw new ArgumentException("Session must contain at least one tab.", nameof(session));
        }

        int selectedIndex = session.ActiveTabIndex;
        if (selectedIndex < 0 || selectedIndex >= session.Tabs.Count)
        {
            selectedIndex = 0;
        }

        var immediate = new StartupRestoreTab(selectedIndex, session.Tabs[selectedIndex]);
        var deferred = session.Tabs
            .Select((tab, index) => new StartupRestoreTab(index, tab))
            .Where(tab => tab.OriginalIndex != selectedIndex)
            .ToArray();

        return new StartupRestorePlan(immediate, deferred);
    }
}
