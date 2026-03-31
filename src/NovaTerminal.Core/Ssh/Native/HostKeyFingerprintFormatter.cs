namespace NovaTerminal.Core.Ssh.Native;

public static class HostKeyFingerprintFormatter
{
    public static string Normalize(string fingerprint)
    {
        string normalized = fingerprint?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        const string prefix = "SHA256:";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return prefix + normalized[prefix.Length..].Trim();
        }

        return normalized;
    }
}
