using System;
using System.Reflection;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Test-only helper for constructing a <see cref="StartupPerformanceTracker"/>
/// with a controllable clock. Uses the tracker's <c>internal</c> test ctor
/// (visible via <c>InternalsVisibleTo</c>) so tests do not depend on
/// wall-clock <see cref="System.Diagnostics.Stopwatch"/> timing.
/// </summary>
internal static class TestTrackerFactory
{
    public static (StartupPerformanceTracker tracker, long[] clock) CreateTracker()
    {
        var clock = new long[] { 0L };
        Func<long> ticks = () => clock[0];
        var tracker = (StartupPerformanceTracker)Activator.CreateInstance(
            typeof(StartupPerformanceTracker),
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new object?[] { ticks, 0L, 1000L, null },
            null)!;
        return (tracker, clock);
    }
}
