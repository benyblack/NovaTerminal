using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NovaTerminal.Shell.Secrets
{
    /// <summary>Selects the platform secret store and performs one-time legacy cleanup.</summary>
    public static class SecretStore
    {
        public static ISecretStore CreateDefault()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsCredentialStore();
            }

            // Pre-#100 builds wrote a weakly-encrypted vault.dat on Linux/macOS.
            // Delete it; it is never read or migrated.
            DeleteLegacyVaultFile(AppPaths.LegacyVaultFilePath);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new MacKeychainStore();
            }

            return new LinuxSecretStore();
        }

        public static void DeleteLegacyVaultFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    System.Diagnostics.Debug.WriteLine("[Vault] Deleted legacy weakly-encrypted vault.dat.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Vault] Legacy vault delete failed: {ex.Message}");
            }
        }
    }
}
