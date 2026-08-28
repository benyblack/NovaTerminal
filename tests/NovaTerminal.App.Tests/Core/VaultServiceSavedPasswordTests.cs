using System;
using NovaTerminal.Shell;
using NovaTerminal.Shell.Secrets;

namespace NovaTerminal.Tests.Core;

// Note: both TerminalProfile and ConnectionType live in NovaTerminal.Shell
// (src/NovaTerminal.App/Shell/TerminalProfile.cs). Do not add a
// `using NovaTerminal.Platform;` — it is not needed here.

public class VaultServiceSavedPasswordTests
{
    private sealed class UnavailableStore : ISecretStore
    {
        public bool IsAvailable => false;
        public string? Read(string key) => "should-not-be-read";
        public void Write(string key, string value) => throw new InvalidOperationException("must not write");
        public bool Delete(string key) => throw new InvalidOperationException("must not delete");
    }

    private static TerminalProfile CreateProfile(string name = "Prod")
    {
        return new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Name = name,
            SshHost = "prod.internal",
            SshUser = "ops"
        };
    }

    [Fact]
    public void IsVaultAvailable_ReflectsStore()
    {
        Assert.True(new VaultService(new InMemorySecretStore()).IsVaultAvailable);
        Assert.False(new VaultService(new UnavailableStore()).IsVaultAvailable);
    }

    [Fact]
    public void HasSavedPassword_IsFalse_WhenNothingStored()
    {
        var vault = new VaultService(new InMemorySecretStore());
        Assert.False(vault.HasSavedPassword(CreateProfile()));
    }

    [Fact]
    public void HasSavedPassword_IsTrue_ForCanonicalKey()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();

        store.Write(VaultService.GetCanonicalSshProfileKey(profile.Id), "secret");

        Assert.True(vault.HasSavedPassword(profile));
    }

    [Fact]
    public void HasSavedPassword_IsTrue_ForPerProfileLegacyKey()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();

        store.Write($"profile_{profile.Id}_password", "secret");

        Assert.True(vault.HasSavedPassword(profile));
    }

    [Fact]
    public void HasSavedPassword_IsFalse_ForSharedAliasOnly()
    {
        // The shared SSH:{user}@{host} alias may be resolved by sibling profiles
        // on the same host, so it is deliberately outside this profile's scope.
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();

        store.Write("SSH:ops@prod.internal", "secret");

        Assert.False(vault.HasSavedPassword(profile));
    }

    [Fact]
    public void HasSavedPassword_DoesNotWriteToStore()
    {
        // Must not go through ResolveSshPasswordForProfile, which migrates a
        // legacy hit to the canonical key as a side effect.
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();
        string legacyKey = $"profile_{profile.Id}_password";

        store.Write(legacyKey, "secret");
        Assert.True(vault.HasSavedPassword(profile));

        Assert.Null(store.Read(VaultService.GetCanonicalSshProfileKey(profile.Id)));
        Assert.Equal("secret", store.Read(legacyKey));
    }

    [Fact]
    public void ForgetSavedPassword_ClearsCanonicalAndPerProfileLegacyKeys()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();
        string canonicalKey = VaultService.GetCanonicalSshProfileKey(profile.Id);
        string legacyIdKey = $"profile_{profile.Id}_password";
        string legacyNamedKey = "SSH:Prod:ops@prod.internal";

        store.Write(canonicalKey, "a");
        store.Write(legacyIdKey, "b");
        store.Write(legacyNamedKey, "c");

        Assert.True(vault.ForgetSavedPassword(profile));

        Assert.Null(store.Read(canonicalKey));
        Assert.Null(store.Read(legacyIdKey));
        Assert.Null(store.Read(legacyNamedKey));
        Assert.False(vault.HasSavedPassword(profile));
    }

    [Fact]
    public void ForgetSavedPassword_LeavesSharedAliasIntact()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        TerminalProfile profile = CreateProfile();

        store.Write(VaultService.GetCanonicalSshProfileKey(profile.Id), "mine");
        store.Write("SSH:ops@prod.internal", "shared");

        Assert.True(vault.ForgetSavedPassword(profile));

        Assert.Equal("shared", store.Read("SSH:ops@prod.internal"));
    }

    [Fact]
    public void ForgetSavedPassword_ReturnsFalse_WhenNothingStored()
    {
        var vault = new VaultService(new InMemorySecretStore());
        Assert.False(vault.ForgetSavedPassword(CreateProfile()));
    }

    [Fact]
    public void SavedPasswordMembers_WhenVaultUnavailable_AreSafeNoOps()
    {
        // UnavailableStore throws on Read/Write/Delete; the IsAvailable guards in
        // GetSecret/RemoveSecret must short-circuit before touching it.
        var vault = new VaultService(new UnavailableStore());
        TerminalProfile profile = CreateProfile();

        Assert.False(vault.IsVaultAvailable);
        Assert.False(vault.HasSavedPassword(profile));
        Assert.False(vault.ForgetSavedPassword(profile));
    }

    [Fact]
    public void ForgetSavedPassword_Throws_OnNullProfile()
    {
        var vault = new VaultService(new InMemorySecretStore());
        Assert.Throws<ArgumentNullException>(() => vault.ForgetSavedPassword(null!));
    }
}
