using NovaTerminal.Core.Ssh.Native;

namespace NovaTerminal.Core.Tests.Ssh;

public sealed class NativeKnownHostsStoreTests
{
    [Fact]
    public void TrustHost_FirstTimePersistsEntryAndTrustedLookup()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            var store = new NativeKnownHostsStore(path);

            Assert.Equal(
                NativeKnownHostMatch.Unknown,
                store.CheckHost("example.internal", 22, "ssh-ed25519", " sha256:test-fingerprint "));

            store.TrustHost("example.internal", 22, "ssh-ed25519", " sha256:test-fingerprint ");

            Assert.Equal(
                NativeKnownHostMatch.Trusted,
                store.CheckHost("EXAMPLE.INTERNAL", 22, "ssh-ed25519", "SHA256:test-fingerprint"));

            string json = File.ReadAllText(path);
            Assert.Contains("example.internal", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passphrase", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CheckHost_WhenFingerprintChanges_ReturnsMismatch()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempRoot, "native_known_hosts.json");
            var store = new NativeKnownHostsStore(path);
            store.TrustHost("example.internal", 22, "ssh-ed25519", "SHA256:trusted");

            Assert.Equal(
                NativeKnownHostMatch.Mismatch,
                store.CheckHost("example.internal", 22, "ssh-ed25519", "SHA256:changed"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Formatter_NormalizesFingerprintDeterministically()
    {
        string normalized = HostKeyFingerprintFormatter.Normalize(" sha256:AbCdEf123= ");

        Assert.Equal("SHA256:AbCdEf123=", normalized);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_known_hosts_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
