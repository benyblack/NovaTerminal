using System;
using NovaTerminal.Shell.Native;

namespace NovaTerminal.Shell.Secrets
{
    /// <summary>
    /// Windows secret store backed by the Win32 Credential Manager (per-user, DPAPI-protected).
    /// </summary>
    public sealed class WindowsCredentialStore : ISecretStore
    {
        public bool IsAvailable => true;

        public string? Read(string key)
        {
            string target = ToTarget(key);
            var cred = Win32CredentialManager.Read(target);
            return cred?.Password;
        }

        public void Write(string key, string value)
        {
            string target = ToTarget(key);
            Win32CredentialManager.Write(target, ExtractUsername(target), value);
        }

        public bool Delete(string key) => Win32CredentialManager.Delete(ToTarget(key));

        private static string ToTarget(string key)
            => key.StartsWith("NovaTerminal:", StringComparison.Ordinal) ? key : $"NovaTerminal:{key}";

        // Mirrors the legacy VaultService.SetSecret username extraction:
        // "NovaTerminal:SSH:User@Host" or "NovaTerminal:SSH:ProfileName:User@Host".
        private static string ExtractUsername(string target)
        {
            string username = "User";
            if (!target.Contains(":SSH:", StringComparison.Ordinal))
            {
                return username;
            }

            string sshPart = target.Substring(target.IndexOf(":SSH:", StringComparison.Ordinal) + 5);
            int lastAt = sshPart.LastIndexOf('@');
            if (lastAt > 0)
            {
                string preHost = sshPart.Substring(0, lastAt);
                int lastColon = preHost.LastIndexOf(':');
                username = lastColon >= 0 ? preHost.Substring(lastColon + 1) : preHost;
            }

            return username;
        }
    }
}
