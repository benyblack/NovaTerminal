using NovaTerminal.Shell.Secrets;

namespace NovaTerminal.Tests.Core;

public class InMemorySecretStoreTests
{
    [Fact]
    public void IsAvailable_IsTrue()
    {
        var store = new InMemorySecretStore();
        Assert.True(store.IsAvailable);
    }

    [Fact]
    public void Write_ThenRead_ReturnsValue()
    {
        var store = new InMemorySecretStore();
        store.Write("k", "v");
        Assert.Equal("v", store.Read("k"));
    }

    [Fact]
    public void Read_MissingKey_ReturnsNull()
    {
        var store = new InMemorySecretStore();
        Assert.Null(store.Read("missing"));
    }

    [Fact]
    public void Write_OverwritesExistingValue()
    {
        var store = new InMemorySecretStore();
        store.Write("k", "v1");
        store.Write("k", "v2");
        Assert.Equal("v2", store.Read("k"));
    }

    [Fact]
    public void Delete_ExistingKey_ReturnsTrueAndRemoves()
    {
        var store = new InMemorySecretStore();
        store.Write("k", "v");
        Assert.True(store.Delete("k"));
        Assert.Null(store.Read("k"));
    }

    [Fact]
    public void Delete_MissingKey_ReturnsFalse()
    {
        var store = new InMemorySecretStore();
        Assert.False(store.Delete("missing"));
    }
}
