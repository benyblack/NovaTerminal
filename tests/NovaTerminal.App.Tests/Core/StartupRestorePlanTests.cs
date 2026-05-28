using NovaTerminal.Core;
using NovaTerminal.VT;
using NovaTerminal.Pty;

namespace NovaTerminal.Tests.Core;

public sealed class StartupRestorePlanTests
{
    [Fact]
    public void Create_PrioritizesSelectedTabAndPreservesDeferredOrder()
    {
        var session = new NovaSession
        {
            ActiveTabIndex = 1,
            Tabs =
            {
                new TabSession { Title = "One" },
                new TabSession { Title = "Two" },
                new TabSession { Title = "Three" }
            }
        };

        StartupRestorePlan plan = StartupRestorePlan.Create(session);

        Assert.Equal(1, plan.ImmediateTab.OriginalIndex);
        Assert.Equal("Two", plan.ImmediateTab.Tab.Title);
        Assert.Equal(new[] { 0, 2 }, plan.DeferredTabs.Select(static tab => tab.OriginalIndex).ToArray());
        Assert.Equal(new[] { "One", "Three" }, plan.DeferredTabs.Select(static tab => tab.Tab.Title).ToArray());
    }
}
