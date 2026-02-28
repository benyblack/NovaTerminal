using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Storage;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Tests.Ssh;

public sealed class SshLegacyProfileMigrationTests
{
    [Fact]
    public void MigrateLegacyProfiles_MovesSshProfilesToStoreAndKeepsLocalProfilesInSettings()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(storePath);
            var migration = new SshLegacyProfileMigrationService(store);

            var localId = Guid.Parse("ec73be98-eb3f-4f55-a01e-24a31957e2fb");
            var sshId = Guid.Parse("77ec18f4-99f1-4437-bdfe-d61728638e8f");
            var settings = new TerminalSettings
            {
                Profiles = new List<TerminalProfile>
                {
                    new TerminalProfile
                    {
                        Id = localId,
                        Name = "PowerShell",
                        Type = ConnectionType.Local,
                        Command = "pwsh.exe"
                    },
                    new TerminalProfile
                    {
                        Id = sshId,
                        Name = "Prod SSH",
                        Type = ConnectionType.SSH,
                        SshHost = "prod.internal",
                        SshUser = "ops",
                        SshPort = 2200,
                        Group = "Prod",
                        Notes = "critical",
                        AccentColor = "#FFAA11",
                        Tags = new List<string> { "favorite", "prod" },
                        UseSshAgent = false,
                        IdentityFilePath = "C:\\keys\\prod"
                    }
                },
                DefaultProfileId = sshId
            };

            bool changed = migration.MigrateLegacyProfiles(settings);

            Assert.True(changed);
            Assert.Single(settings.Profiles);
            Assert.Equal(ConnectionType.Local, settings.Profiles[0].Type);
            Assert.Equal(localId, settings.Profiles[0].Id);
            Assert.Equal(localId, settings.DefaultProfileId);

            var migrated = store.GetProfile(sshId);
            Assert.NotNull(migrated);
            Assert.Equal("prod.internal", migrated!.Host);
            Assert.Equal("ops", migrated.User);
            Assert.Equal(2200, migrated.Port);
            Assert.Equal("Prod", migrated.GroupPath);
            Assert.Equal("critical", migrated.Notes);
            Assert.Equal("#FFAA11", migrated.AccentColor);
            Assert.Contains("favorite", migrated.Tags, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacyProfiles_NoLegacySsh_ReturnsFalse()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(storePath);
            var migration = new SshLegacyProfileMigrationService(store);

            var localId = Guid.Parse("28d69fd4-b59e-4e34-b5e6-fe8fbe78b94d");
            var settings = new TerminalSettings
            {
                Profiles = new List<TerminalProfile>
                {
                    new TerminalProfile
                    {
                        Id = localId,
                        Name = "PowerShell",
                        Type = ConnectionType.Local,
                        Command = "pwsh.exe"
                    }
                },
                DefaultProfileId = localId
            };

            bool changed = migration.MigrateLegacyProfiles(settings);

            Assert.False(changed);
            Assert.Single(settings.Profiles);
            Assert.Empty(store.GetProfiles());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_migration_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
