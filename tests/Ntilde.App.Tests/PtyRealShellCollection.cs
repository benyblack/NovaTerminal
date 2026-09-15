using Xunit;

namespace NovaTerminal.Tests
{
    /// <summary>
    /// Serializes every test class that spawns a real shell through a PTY.
    ///
    /// xUnit runs test classes in parallel by default, and this assembly configures no
    /// parallelism limits. That was harmless while only PtySmokeTests and
    /// PtyThreadLifecycleTests existed, but the read-failure tests added in #215 spawn
    /// four more concurrent sessions whose output is deliberately never drained. On a
    /// 2-core Windows runner that was enough to starve
    /// PtySmokeTests.RustPtySession_FlightRecording_CapturesOutputAndExports, which needs
    /// a live shell to actually produce output within its timeout — it began failing on
    /// main the moment those tests landed, while passing in the PR run.
    ///
    /// Sharing one collection makes these classes run one at a time. It costs wall-clock
    /// time in exchange for tests that assert on real subprocess timing being isolated
    /// from each other, which is the only way they can be meaningful.
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class PtyRealShellCollection
    {
        public const string Name = "PTY real shell";
    }
}
