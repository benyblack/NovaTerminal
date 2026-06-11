using NovaTerminal.Shell;
using NovaTerminal.Shell.Secrets;

namespace NovaTerminal.Tests.Core;

public class VaultServiceDisabledModeTests
{
    private sealed class UnavailableStore : ISecretStore
    {
        public bool IsAvailable => false;
        public string? Read(string key) => "should-not-be-read";
        public void Write(string key, string value) => throw new InvalidOperationException("must not write");
        public bool Delete(string key) => throw new InvalidOperationException("must not delete");
    }

    [Fact]
    public void PersistenceAvailable_ReflectsStore()
    {
        Assert.False(new VaultService(new UnavailableStore()).PersistenceAvailable);
        Assert.True(new VaultService(new InMemorySecretStore()).PersistenceAvailable);
    }

    [Fact]
    public void GetSecret_WhenUnavailable_ReturnsNullWithoutReadingStore()
    {
        var vault = new VaultService(new UnavailableStore());
        Assert.Null(vault.GetSecret("any"));
    }

    [Fact]
    public void SetSecret_WhenUnavailable_IsNoOpAndDoesNotThrow()
    {
        var vault = new VaultService(new UnavailableStore());
        vault.SetSecret("k", "v"); // must not throw
    }

    [Fact]
    public void RemoveSecret_WhenUnavailable_ReturnsFalseWithoutThrowing()
    {
        var vault = new VaultService(new UnavailableStore());
        Assert.False(vault.RemoveSecret("k"));
    }

    [Fact]
    public void RoundTrip_ThroughInjectedStore_Works()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        vault.SetSecret("k", "v");
        Assert.Equal("v", vault.GetSecret("k"));
        Assert.True(vault.RemoveSecret("k"));
        Assert.Null(vault.GetSecret("k"));
    }
}
