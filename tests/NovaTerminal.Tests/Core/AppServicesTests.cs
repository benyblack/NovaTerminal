using System;
using System.Collections.Generic;
using NovaTerminal.Core;
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
