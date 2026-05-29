using NovaTerminal.Shell;
using System;
using NovaTerminal.Core;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Test-only helper for constructing a <see cref="StartupPerformanceTracker"/>
/// with a controllable clock. Uses the tracker's <c>internal</c> test ctor,
/// which the test project can call directly via <c>InternalsVisibleTo</c>.
/// </summary>
internal static class TestTrackerFactory
{
    public static (StartupPerformanceTracker tracker, long[] clock) CreateTracker()
    {
        var clock = new long[] { 0L };
        Func<long> ticks = () => clock[0];
        var tracker = new StartupPerformanceTracker(ticks, 0L, 1000L, metricsWriter: null);
        return (tracker, clock);
    }
}
