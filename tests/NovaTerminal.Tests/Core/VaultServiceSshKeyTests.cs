using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public class VaultServiceSshKeyTests
{
    [Fact]
    public void CanonicalKey_UsesProfileId()
    {
        var profileId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        string key = VaultService.GetCanonicalSshProfileKey(profileId);

        Assert.Equal("SSH:PROFILE:11111111-2222-3333-4444-555555555555", key);
    }

    [Fact]
    public void ApplyRememberPasswordPreference_RemovesCanonicalProfileSecret_WhenDisabled()
    {
        TerminalProfile profile = CreateProfile();
        string canonical = VaultService.GetCanonicalSshProfileKey(profile.Id);
        string legacy = $"SSH:{profile.Name}:{profile.SshUser}@{profile.SshHost}";

        var store = new Dictionary<string, string>
        {
            [canonical] = "canonical-secret",
            [legacy] = "legacy-secret"
        };

        VaultService.ApplyRememberPasswordPreference(
            profile.Id,
            rememberPasswordInVault: false,
            password: null,
            removeSecret: key => store.Remove(key),
            writeSecret: (key, value) => store[key] = value);

        Assert.False(store.ContainsKey(canonical));
        Assert.True(store.ContainsKey(legacy));
    }

    [Fact]
    public void ApplyRememberPasswordPreference_RemovesProfileScopedSecrets_WhenDisabled()
    {
        TerminalProfile profile = CreateProfile();
        string canonical = VaultService.GetCanonicalSshProfileKey(profile.Id);
        string namedLegacy = $"SSH:{profile.Name}:{profile.SshUser}@{profile.SshHost}";
        string unnamedLegacy = $"SSH:{profile.SshUser}@{profile.SshHost}";
        string idLegacy = $"profile_{profile.Id}_password";

        var store = new Dictionary<string, string>
        {
            [canonical] = "canonical-secret",
            [namedLegacy] = "named-legacy-secret",
            [unnamedLegacy] = "unnamed-legacy-secret",
            [idLegacy] = "id-legacy-secret"
        };

        VaultService.ApplyRememberPasswordPreference(
            profile,
            rememberPasswordInVault: false,
            password: null,
            removeSecret: key => store.Remove(key),
            writeSecret: (key, value) => store[key] = value);

        Assert.DoesNotContain(canonical, store.Keys);
        Assert.DoesNotContain(namedLegacy, store.Keys);
        Assert.DoesNotContain(idLegacy, store.Keys);
        Assert.Contains(unnamedLegacy, store.Keys);
    }

    [Fact]
    public void ApplyRememberPasswordPreference_PreservesSharedLegacyAlias_ForOtherProfiles()
    {
        TerminalProfile profile = CreateProfile();
        TerminalProfile siblingProfile = new()
        {
            Id = Guid.Parse("15edb0b0-7d62-4e7a-977f-fd36403f48bd"),
            Name = "Prod 2",
            Type = ConnectionType.SSH,
            SshUser = profile.SshUser,
            SshHost = profile.SshHost
        };
        string sharedLegacy = $"SSH:{profile.SshUser}@{profile.SshHost}";

        var store = new Dictionary<string, string>
        {
            [sharedLegacy] = "shared-secret"
        };

        VaultService.ApplyRememberPasswordPreference(
            profile,
            rememberPasswordInVault: false,
            password: null,
            removeSecret: key => store.Remove(key),
            writeSecret: (key, value) => store[key] = value);

        Assert.Equal("shared-secret", store[sharedLegacy]);

        string? siblingSecret = VaultService.ResolveSshPasswordForProfile(
            siblingProfile,
            key => store.TryGetValue(key, out var value) ? value : null,
            (key, value) => store[key] = value);

        Assert.Equal("shared-secret", siblingSecret);
    }

    [Fact]
    public void FileBackedVaultInstances_SeeAndRemoveEachOthersSecrets()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string vaultPath = Path.Combine(tempRoot, "vault.dat");
            TerminalProfile profile = CreateProfile();

            var writer = new VaultService(vaultPath);
            var remover = new VaultService(vaultPath);

            writer.SetSshPasswordForProfile(profile, "shared-secret");

            Assert.Equal("shared-secret", remover.GetSshPasswordForProfile(profile));

            remover.ApplyRememberPasswordPreference(profile, rememberPasswordInVault: false);

            Assert.Null(writer.GetSshPasswordForProfile(profile));
            Assert.Null(remover.GetSshPasswordForProfile(profile));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void FileBackedReload_PreservesCachedSecrets_WhenLoadFails()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string vaultPath = Path.Combine(tempRoot, "vault.dat");
            var vault = new VaultService(vaultPath);

            vault.SetSecret("alpha", "one");
            File.WriteAllBytes(vaultPath, [0x01, 0x02, 0x03, 0x04]);

            vault.SetSecret("beta", "two");

            var reloaded = new VaultService(vaultPath);
            Assert.Equal("one", reloaded.GetSecret("alpha"));
            Assert.Equal("two", reloaded.GetSecret("beta"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ApplyRememberPasswordPreference_LeavesCanonicalProfileSecretUntouched_WhenEnabledWithoutPassword()
    {
        TerminalProfile profile = CreateProfile();
        string canonical = VaultService.GetCanonicalSshProfileKey(profile.Id);

        var store = new Dictionary<string, string>
        {
            [canonical] = "canonical-secret"
        };

        VaultService.ApplyRememberPasswordPreference(
            profile.Id,
            rememberPasswordInVault: true,
            password: null,
            removeSecret: key => store.Remove(key),
            writeSecret: (key, value) => store[key] = value);

        Assert.Equal("canonical-secret", store[canonical]);
    }

    [Fact]
    public void Resolve_PrefersCanonicalAndDoesNotMigrate()
    {
        TerminalProfile profile = CreateProfile();
        string canonical = VaultService.GetCanonicalSshProfileKey(profile.Id);
        int writes = 0;

        var store = new Dictionary<string, string>
        {
            [canonical] = "canonical-secret",
            [$"SSH:{profile.Name}:{profile.SshUser}@{profile.SshHost}"] = "legacy-secret"
        };

        string? result = VaultService.ResolveSshPasswordForProfile(
            profile,
            key => store.TryGetValue(key, out var value) ? value : null,
            (key, value) =>
            {
                writes++;
                store[key] = value;
            });

        Assert.Equal("canonical-secret", result);
        Assert.Equal(0, writes);
    }

    [Theory]
    [MemberData(nameof(LegacyKeyCases))]
    public void Resolve_Migrates_FromEachLegacyKey(string legacyKey)
    {
        TerminalProfile profile = CreateProfile();
        string canonical = VaultService.GetCanonicalSshProfileKey(profile.Id);
        int writes = 0;

        var store = new Dictionary<string, string>
        {
            [legacyKey] = "legacy-secret"
        };

        string? result = VaultService.ResolveSshPasswordForProfile(
            profile,
            key => store.TryGetValue(key, out var value) ? value : null,
            (key, value) =>
            {
                writes++;
                store[key] = value;
            });

        Assert.Equal("legacy-secret", result);
        Assert.Equal(1, writes);
        Assert.True(store.ContainsKey(canonical));
        Assert.Equal("legacy-secret", store[canonical]);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoKnownKeyExists()
    {
        TerminalProfile profile = CreateProfile();
        int writes = 0;

        string? result = VaultService.ResolveSshPasswordForProfile(
            profile,
            _ => null,
            (_, _) => writes++);

        Assert.Null(result);
        Assert.Equal(0, writes);
    }

    public static IEnumerable<object[]> LegacyKeyCases()
    {
        TerminalProfile profile = CreateProfile();
        yield return new object[] { $"SSH:{profile.Name}:{profile.SshUser}@{profile.SshHost}" };
        yield return new object[] { $"SSH:{profile.SshUser}@{profile.SshHost}" };
        yield return new object[] { $"profile_{profile.Id}_password" };
    }

    private static TerminalProfile CreateProfile()
    {
        return new TerminalProfile
        {
            Id = Guid.Parse("2a3db873-1437-4f8b-a8a6-a643f597f6bd"),
            Name = "Prod",
            Type = ConnectionType.SSH,
            SshUser = "alice",
            SshHost = "example.internal"
        };
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_vault_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
