using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.OpenSsh;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class SshLaunchPlannerTests
{
    [Fact]
    public void Plan_UsesGeneratedConfigAliasLaunchShape()
    {
        string root = CreateTempDirectory();
        try
        {
            var store = new JsonSshProfileStore(Path.Combine(root, "profiles.json"));
            var profile = new SshProfile
            {
                Id = Guid.Parse("d17c9e8a-9a56-4b26-9bcf-afc6d6b8c0f3"),
                Host = "example.com"
            };
            store.SaveProfile(profile);

            var compiler = new OpenSshConfigCompiler(root);
            var planner = new SshLaunchPlanner(store, compiler);

            SshLaunchPlan plan = planner.Plan(profile.Id);

            Assert.False(string.IsNullOrWhiteSpace(plan.SshExecutablePath));
            Assert.Equal(profile.Id, plan.ProfileId);
            Assert.Equal($"nova_{profile.Id:N}", plan.Alias);
            Assert.Equal(3, plan.Arguments.Count);
            Assert.Equal("-F", plan.Arguments[0]);
            Assert.Equal(plan.ConfigFilePath, plan.Arguments[1]);
            Assert.Equal(plan.Alias, plan.Arguments[2]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Plan_WithDiagnostics_AppendsVerbosityFlagAfterAlias()
    {
        string root = CreateTempDirectory();
        try
        {
            var store = new JsonSshProfileStore(Path.Combine(root, "profiles.json"));
            var profile = new SshProfile
            {
                Id = Guid.Parse("f3e45de6-895e-4b18-bb6e-7a0265f398db"),
                Host = "example.com"
            };
            store.SaveProfile(profile);

            var compiler = new OpenSshConfigCompiler(root);
            var planner = new SshLaunchPlanner(store, compiler);

            SshLaunchPlan plan = planner.Plan(profile.Id, SshDiagnosticsLevel.VeryVerbose.ToArguments());

            Assert.Equal(4, plan.Arguments.Count);
            Assert.Equal(plan.Alias, plan.Arguments[2]);
            Assert.Equal("-vv", plan.Arguments[3]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_planner_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
