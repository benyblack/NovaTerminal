using NovaTerminal.Shell;
using NovaTerminal.Core;
using NovaTerminal.VT;
using NovaTerminal.Pty;

namespace NovaTerminal.Tests.Core;

public sealed class StartupRestoreCoordinatorTests
{
    [Fact]
    public void RunDeferred_RestoresDeferredTabsInStableOrderAndCompletes()
    {
        var scheduled = new Queue<Action>();
        var restored = new List<int>();
        bool completed = false;

        var coordinator = new StartupRestoreCoordinator(action => scheduled.Enqueue(action));
        var deferredTabs = new[]
        {
            new StartupRestoreTab(0, new TabSession { Title = "One" }),
            new StartupRestoreTab(2, new TabSession { Title = "Three" })
        };

        coordinator.RunDeferred(
            deferredTabs,
            tab => restored.Add(tab.OriginalIndex),
            () => completed = true);

        Assert.False(completed);
        Assert.Single(scheduled);
        scheduled.Dequeue().Invoke();

        Assert.Equal(new[] { 0, 2 }, restored);
        Assert.True(completed);
        Assert.Empty(scheduled);
    }
}
