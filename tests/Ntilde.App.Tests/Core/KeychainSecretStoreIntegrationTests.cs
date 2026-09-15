using System;
using System.Runtime.InteropServices;
using NovaTerminal.Shell.Secrets;

namespace NovaTerminal.Tests.Core;

public class KeychainSecretStoreIntegrationTests
{
    private static ISecretStore? CreatePlatformStore()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacKeychainStore();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new LinuxSecretStore();
        return null; // Windows uses WindowsCredentialStore, covered elsewhere.
    }

    [Fact]
    public void RoundTrip_WhenKeychainAvailable()
    {
        ISecretStore? store = CreatePlatformStore();
        if (store is null || !store.IsAvailable)
        {
            return; // Skip: no platform keyring in this environment.
        }

        string key = "SSH:PROFILE:" + Guid.NewGuid().ToString("D");
        try
        {
            store.Write(key, "integration-secret");
            Assert.Equal("integration-secret", store.Read(key));

            store.Write(key, "updated-secret");
            Assert.Equal("updated-secret", store.Read(key));

            Assert.True(store.Delete(key));
            Assert.Null(store.Read(key));
        }
        finally
        {
            store.Delete(key);
        }
    }
}
