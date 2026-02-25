using System;
using System.IO;
using Xunit;

namespace NovaTerminal.Tests.Tools
{
    public sealed class FixtureUpdatePolicyTests : IDisposable
    {
        private const string UpdateReplayFixturesEnvVar = "UPDATE_REPLAY_FIXTURES";

        public FixtureUpdatePolicyTests()
        {
            Environment.SetEnvironmentVariable(UpdateReplayFixturesEnvVar, null);
        }

        [Fact]
        public void GenerateReplayFixtureIfNeeded_FileMissing_Generates()
        {
            string recPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.rec");
            bool generated = false;

            FixtureUpdatePolicy.GenerateReplayFixtureIfNeeded(recPath, path =>
            {
                generated = true;
                File.WriteAllText(path, "fixture");
            });

            Assert.True(generated);
            Assert.True(File.Exists(recPath));

            File.Delete(recPath);
        }

        [Fact]
        public void GenerateReplayFixtureIfNeeded_FileExists_DoesNotGenerateByDefault()
        {
            string recPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.rec");
            File.WriteAllText(recPath, "fixture");
            bool generated = false;

            FixtureUpdatePolicy.GenerateReplayFixtureIfNeeded(recPath, _ => generated = true);

            Assert.False(generated);

            File.Delete(recPath);
        }

        [Fact]
        public void GenerateReplayFixtureIfNeeded_FileExists_GeneratesWhenOptedIn()
        {
            string recPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.rec");
            File.WriteAllText(recPath, "fixture");
            bool generated = false;
            Environment.SetEnvironmentVariable(UpdateReplayFixturesEnvVar, "1");

            FixtureUpdatePolicy.GenerateReplayFixtureIfNeeded(recPath, _ => generated = true);

            Assert.True(generated);

            File.Delete(recPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(UpdateReplayFixturesEnvVar, null);
        }
    }
}
