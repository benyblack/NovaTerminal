using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class JsonSshProfileStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsProfile()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(storePath);

            var profile = new SshProfile
            {
                Id = Guid.Parse("aeb456e4-8dd2-4b33-a74d-cb473de0323b"),
                Name = "prod",
                Host = "prod.internal",
                User = "svc",
                Port = 2222,
                AuthMode = SshAuthMode.IdentityFile,
                IdentityFilePath = "C:\\keys\\prod_ed25519",
                JumpHops =
                {
                    new SshJumpHop { Host = "jump-1.internal" },
                    new SshJumpHop { Host = "jump-2.internal", User = "ops", Port = 2200 }
                },
                Forwards =
                {
                    new PortForward
                    {
                        Kind = PortForwardKind.Local,
                        BindAddress = "127.0.0.1",
                        SourcePort = 5432,
                        DestinationHost = "db.internal",
                        DestinationPort = 5432
                    }
                },
                ServerAliveIntervalSeconds = 45,
                ServerAliveCountMax = 5,
                ExtraSshArgs = "-o StrictHostKeyChecking=no"
            };

            store.SaveProfile(profile);
            SshProfile? loaded = store.GetProfile(profile.Id);

            Assert.NotNull(loaded);
            Assert.Equal(profile.Name, loaded!.Name);
            Assert.Equal(profile.Host, loaded.Host);
            Assert.Equal(profile.User, loaded.User);
            Assert.Equal(profile.Port, loaded.Port);
            Assert.Equal(profile.AuthMode, loaded.AuthMode);
            Assert.Equal(profile.IdentityFilePath, loaded.IdentityFilePath);
            Assert.Equal(2, loaded.JumpHops.Count);
            Assert.Single(loaded.Forwards);
            Assert.Equal(45, loaded.ServerAliveIntervalSeconds);
            Assert.Equal(5, loaded.ServerAliveCountMax);
            Assert.Equal("-o StrictHostKeyChecking=no", loaded.ExtraSshArgs);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveProfile_DoesNotPersistPasswordOrPassphraseFields()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(storePath);

            store.SaveProfile(new SshProfile
            {
                Name = "prod",
                Host = "prod.internal"
            });

            string json = File.ReadAllText(storePath);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passphrase", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetDefaultStorePath_IsStablePerUserPath()
    {
        string path = JsonSshProfileStore.GetDefaultStorePath();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(localAppData, path, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.StartsWith(localAppData, path, StringComparison.Ordinal);
        }

        Assert.EndsWith(Path.Combine("NovaTerminal", "ssh", "profiles.json"), path);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
