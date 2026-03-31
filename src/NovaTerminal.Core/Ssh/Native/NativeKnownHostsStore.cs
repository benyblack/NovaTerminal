using System.Text.Json;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Core.Ssh.Native;

public enum NativeKnownHostMatch
{
    Unknown = 0,
    Trusted = 1,
    Mismatch = 2
}

public sealed class NativeKnownHostsStore
{
    private readonly object _syncRoot = new();
    private readonly string _storeFilePath;

    public NativeKnownHostsStore(string storeFilePath)
    {
        _storeFilePath = Path.GetFullPath(storeFilePath ?? throw new ArgumentNullException(nameof(storeFilePath)));
    }

    public string StoreFilePath => _storeFilePath;

    public NativeKnownHostMatch CheckHost(string host, int port, string algorithm, string fingerprint)
    {
        lock (_syncRoot)
        {
            KnownHostEntry? existing = LoadEntriesLocked().FirstOrDefault(entry =>
                string.Equals(entry.Host, NormalizeHost(host), StringComparison.OrdinalIgnoreCase) &&
                entry.Port == NormalizePort(port));

            if (existing == null)
            {
                return NativeKnownHostMatch.Unknown;
            }

            bool matches = string.Equals(existing.Algorithm, NormalizeAlgorithm(algorithm), StringComparison.Ordinal) &&
                           string.Equals(existing.Fingerprint, HostKeyFingerprintFormatter.Normalize(fingerprint), StringComparison.Ordinal);

            return matches ? NativeKnownHostMatch.Trusted : NativeKnownHostMatch.Mismatch;
        }
    }

    public void TrustHost(string host, int port, string algorithm, string fingerprint)
    {
        lock (_syncRoot)
        {
            List<KnownHostEntry> entries = LoadEntriesLocked();
            string normalizedHost = NormalizeHost(host);
            int normalizedPort = NormalizePort(port);
            string normalizedAlgorithm = NormalizeAlgorithm(algorithm);
            string normalizedFingerprint = HostKeyFingerprintFormatter.Normalize(fingerprint);

            int existingIndex = entries.FindIndex(entry =>
                string.Equals(entry.Host, normalizedHost, StringComparison.OrdinalIgnoreCase) &&
                entry.Port == normalizedPort);

            var replacement = new KnownHostEntry
            {
                Host = normalizedHost,
                Port = normalizedPort,
                Algorithm = normalizedAlgorithm,
                Fingerprint = normalizedFingerprint,
                TrustedAtUtc = DateTime.UtcNow
            };

            if (existingIndex >= 0)
            {
                entries[existingIndex] = replacement;
            }
            else
            {
                entries.Add(replacement);
            }

            PersistEntriesLocked(entries);
        }
    }

    private List<KnownHostEntry> LoadEntriesLocked()
    {
        if (!File.Exists(_storeFilePath))
        {
            return new List<KnownHostEntry>();
        }

        try
        {
            string json = File.ReadAllText(_storeFilePath);
            return JsonSerializer.Deserialize(json, SshJsonContext.Default.ListKnownHostEntry) ?? new List<KnownHostEntry>();
        }
        catch
        {
            return new List<KnownHostEntry>();
        }
    }

    private void PersistEntriesLocked(List<KnownHostEntry> entries)
    {
        string? directory = Path.GetDirectoryName(_storeFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var ordered = entries
            .OrderBy(entry => entry.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Port)
            .ToList();

        string json = JsonSerializer.Serialize(ordered, SshJsonContext.Default.ListKnownHostEntry);
        File.WriteAllText(_storeFilePath, json);
    }

    private static string NormalizeHost(string host) => host?.Trim() ?? string.Empty;

    private static string NormalizeAlgorithm(string algorithm) => algorithm?.Trim() ?? string.Empty;

    private static int NormalizePort(int port) => port > 0 ? port : 22;
}
