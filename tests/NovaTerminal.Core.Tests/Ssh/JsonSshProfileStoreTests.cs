using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class JsonSshProfileStoreTests
{
    [Fact]
    public void SshProfile_DefaultsBackendKindToOpenSsh()
    {
        var profile = new SshProfile
        {
            Name = "default-backend",
            Host = "default.internal"
        };

        var backendProperty = typeof(SshProfile).GetProperty("BackendKind");

        Assert.NotNull(backendProperty);
        Assert.Equal("OpenSsh", backendProperty!.GetValue(profile)?.ToString());
    }

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
                GroupPath = "Prod/DB",
                Notes = "critical",
                AccentColor = "#3399EE",
                Tags = new List<string> { "zeta", "Favorite", "alpha", "favorite" },
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
            Assert.Equal("Prod/DB", loaded.GroupPath);
            Assert.Equal("critical", loaded.Notes);
            Assert.Equal("#3399EE", loaded.AccentColor);
            Assert.Equal(new[] { "alpha", "Favorite", "zeta" }, loaded.Tags);
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
    public void SaveAndLoad_RoundTripsBackendKind()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(storePath);
            var profile = new SshProfile
            {
                Id = Guid.Parse("16a0f721-b7dd-4870-9628-18f11e9d45d6"),
                Name = "native",
                Host = "native.internal"
            };

            var backendProperty = typeof(SshProfile).GetProperty("BackendKind");
            Assert.NotNull(backendProperty);
            backendProperty!.SetValue(profile, Enum.Parse(backendProperty.PropertyType, "Native"));

            store.SaveProfile(profile);

            SshProfile? loaded = store.GetProfile(profile.Id);

            Assert.NotNull(loaded);
            Assert.Equal("Native", backendProperty.GetValue(loaded!)?.ToString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsRememberPasswordPreference()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(storePath);
            var profile = new SshProfile
            {
                Id = Guid.Parse("9f59e8e2-7a3f-4c02-9a69-8d6a4b37716f"),
                Name = "native",
                Host = "native.internal",
                BackendKind = SshBackendKind.Native,
                RememberPasswordInVault = true
            };

            store.SaveProfile(profile);

            SshProfile? loaded = store.GetProfile(profile.Id);

            Assert.NotNull(loaded);
            Assert.True(loaded!.RememberPasswordInVault);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void NormalizeRememberPasswordPreference_PreservesNativeProfiles()
    {
        var profile = new SshProfile
        {
            Name = "native",
            Host = "native.internal",
            BackendKind = SshBackendKind.Native,
            RememberPasswordInVault = true
        };

        SshProfileNormalizer.NormalizeRememberPasswordPreference(profile);

        Assert.True(profile.RememberPasswordInVault);
    }

    [Fact]
    public void NormalizeRememberPasswordPreference_PreservesLegacyOpenSshState()
    {
        var profile = new SshProfile
        {
            Name = "openssh",
            Host = "openssh.internal",
            BackendKind = SshBackendKind.OpenSsh,
            RememberPasswordInVault = true
        };

        SshProfileNormalizer.NormalizeRememberPasswordPreference(profile);

        Assert.True(profile.RememberPasswordInVault);
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
            Assert.DoesNotContain("\"Password\":", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"Passphrase\":", json, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void LoadDocument_WhenJsonIsCorrupt_QuarantinesFileAndReturnsEmptyStore()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(tempRoot, "profiles.json");
            File.WriteAllText(storePath, "{ \"Profiles\": [ invalid_json_here ] }");

            var store = new JsonSshProfileStore(storePath);
            IReadOnlyList<SshProfile> profiles = store.GetProfiles(); // Will trigger LoadDocumentLocked

            Assert.Empty(profiles);

            // Store file should be missing due to quarantine
            Assert.False(File.Exists(storePath));

            // Quarantined file should exist
            string[] corruptFiles = Directory.GetFiles(tempRoot, "profiles.json.corrupt.*.json");
            Assert.Single(corruptFiles);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveProfile_UsesAtomicWriteAndDoesNotLeaveTempFiles()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(tempRoot, "profiles.json");
            var store = new JsonSshProfileStore(storePath);

            var profile = new SshProfile
            {
                Id = Guid.NewGuid(),
                Name = "atomic-test",
                Host = "test.internal"
            };

            store.SaveProfile(profile);

            Assert.True(File.Exists(storePath));
            string[] tempFiles = Directory.GetFiles(tempRoot, "*.tmp");
            Assert.Empty(tempFiles);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
