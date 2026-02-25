using System;
using System.IO;

namespace NovaTerminal.Tests.Tools
{
    public static class FixtureUpdatePolicy
    {
        public const string UpdateReplayFixturesEnvironmentVariable = "UPDATE_REPLAY_FIXTURES";
        public const string UpdateSnapshotsEnvironmentVariable = "UPDATE_SNAPSHOTS";

        public static bool ShouldUpdateReplayFixtures()
            => Environment.GetEnvironmentVariable(UpdateReplayFixturesEnvironmentVariable) == "1";

        public static bool ShouldUpdateSnapshots()
            => Environment.GetEnvironmentVariable(UpdateSnapshotsEnvironmentVariable) == "1";

        public static void GenerateReplayFixtureIfNeeded(string recPath, Action<string> generate)
        {
            if (!File.Exists(recPath) || ShouldUpdateReplayFixtures())
            {
                generate(recPath);
            }
        }
    }
}
