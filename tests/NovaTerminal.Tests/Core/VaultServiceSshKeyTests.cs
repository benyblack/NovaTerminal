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
}
