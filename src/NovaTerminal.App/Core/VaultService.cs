using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace NovaTerminal.Core
{
    public interface ISshPasswordVault
    {
        void ApplyRememberPasswordPreference(Guid profileId, bool rememberPasswordInVault, string? password = null);
        void ApplyRememberPasswordPreference(TerminalProfile profile, bool rememberPasswordInVault, string? password = null);
    }

    public class VaultService
        : ISshPasswordVault
    {
        private const string AppName = "NovaTerminal";
        private const string VaultFileName = "vault.dat";
        private readonly string _vaultPath;
        private Dictionary<string, string> _secrets;

#pragma warning disable CA1416 // Validate platform compatibility

        public VaultService(string? vaultPath = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(vaultPath))
                {
                    _vaultPath = vaultPath;
                    string? vaultDirectory = Path.GetDirectoryName(_vaultPath);
                    if (!string.IsNullOrEmpty(vaultDirectory) && !Directory.Exists(vaultDirectory))
                    {
                        Directory.CreateDirectory(vaultDirectory);
                    }

                    _secrets = new Dictionary<string, string>();
                    Load();
                    return;
                }

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string directory = Path.Combine(appData, AppName);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                _vaultPath = Path.Combine(directory, VaultFileName);
                _secrets = new Dictionary<string, string>();

                Load();
            }
            catch
            {
                // Fallback if filesystem access fails
                _vaultPath = string.Empty;
                _secrets = new Dictionary<string, string>();
            }
        }

        public static string GetCanonicalSshProfileKey(Guid profileId)
        {
            return $"SSH:PROFILE:{profileId:D}";
        }

        public static void ApplyRememberPasswordPreference(
            Guid profileId,
            bool rememberPasswordInVault,
            string? password,
            Action<string> removeSecret,
            Action<string, string> writeSecret)
        {
            ArgumentNullException.ThrowIfNull(removeSecret);
            ArgumentNullException.ThrowIfNull(writeSecret);

            string canonicalKey = GetCanonicalSshProfileKey(profileId);
            if (!rememberPasswordInVault)
            {
                removeSecret(canonicalKey);
                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                return;
            }

            writeSecret(canonicalKey, password);
        }

        public static void ApplyRememberPasswordPreference(
            TerminalProfile profile,
            bool rememberPasswordInVault,
            string? password,
            Action<string> removeSecret,
            Action<string, string> writeSecret)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(removeSecret);
            ArgumentNullException.ThrowIfNull(writeSecret);

            if (!rememberPasswordInVault)
            {
                foreach (string key in GetSshPasswordKeysForProfile(profile))
                {
                    removeSecret(key);
                }

                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                return;
            }

            writeSecret(GetCanonicalSshProfileKey(profile.Id), password);
        }

        public void ApplyRememberPasswordPreference(Guid profileId, bool rememberPasswordInVault, string? password = null)
        {
            ApplyRememberPasswordPreference(
                profileId,
                rememberPasswordInVault,
                password,
                key => _ = RemoveSecret(key),
                SetSecret);
        }

        public void ApplyRememberPasswordPreference(TerminalProfile profile, bool rememberPasswordInVault, string? password = null)
        {
            ApplyRememberPasswordPreference(
                profile,
                rememberPasswordInVault,
                password,
                key => _ = RemoveSecret(key),
                SetSecret);
        }

        public static IEnumerable<string> GetLegacySshKeys(TerminalProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            string name = profile.Name?.Trim() ?? string.Empty;
            string user = profile.SshUser?.Trim() ?? string.Empty;
            string host = profile.SshHost?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(host))
            {
                if (!string.IsNullOrEmpty(name))
                {
                    yield return $"SSH:{name}:{user}@{host}";
                }

                yield return $"SSH:{user}@{host}";
            }

            yield return $"profile_{profile.Id}_password";
        }

        public static IEnumerable<string> GetSshPasswordKeysForProfile(TerminalProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            yield return GetCanonicalSshProfileKey(profile.Id);
            foreach (string legacyKey in GetLegacySshKeys(profile))
            {
                yield return legacyKey;
            }
        }

        public static string? ResolveSshPasswordForProfile(
            TerminalProfile profile,
            Func<string, string?> readSecret,
            Action<string, string> writeSecret)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(readSecret);
            ArgumentNullException.ThrowIfNull(writeSecret);

            string canonicalKey = GetCanonicalSshProfileKey(profile.Id);
            string? canonical = readSecret(canonicalKey);
            if (!string.IsNullOrEmpty(canonical))
            {
                return canonical;
            }

            foreach (string legacyKey in GetLegacySshKeys(profile))
            {
                string? legacy = readSecret(legacyKey);
                if (!string.IsNullOrEmpty(legacy))
                {
                    writeSecret(canonicalKey, legacy);
                    return legacy;
                }
            }

            return null;
        }

        public string? GetSshPasswordForProfile(TerminalProfile profile)
        {
            return ResolveSshPasswordForProfile(profile, GetSecret, SetSecret);
        }

        public void SetSshPasswordForProfile(TerminalProfile profile, string? password)
        {
            ArgumentNullException.ThrowIfNull(profile);
            string canonicalKey = GetCanonicalSshProfileKey(profile.Id);

            if (string.IsNullOrEmpty(password))
            {
                RemoveSecret(canonicalKey);
                return;
            }

            SetSecret(canonicalKey, password);
        }

        public void SetSecret(string key, string value)
        {
            ReloadIfFileBacked();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Native Windows Credential Manager
                // Key format usually: "NovaTerminal:SSH:User@Host"
                string target = key.StartsWith("NovaTerminal:") ? key : $"NovaTerminal:{key}";
                // Username is part of the target key in our schema, but CredWrite needs a username field.
                // We'll extract it or just use a placeholder.
                string username = "User";

                // Extract username from key format:
                // "NovaTerminal:SSH:User@Host" (Old)
                // "NovaTerminal:SSH:ProfileName:User@Host" (New)
                if (target.Contains(":SSH:"))
                {
                    string sshPart = target.Substring(target.IndexOf(":SSH:") + 5);
                    // sshPart could be "User@Host" or "ProfileName:User@Host"

                    int lastAt = sshPart.LastIndexOf('@');
                    if (lastAt > 0)
                    {
                        string preHost = sshPart.Substring(0, lastAt); // "User" or "ProfileName:User"
                        int lastColon = preHost.LastIndexOf(':');
                        if (lastColon >= 0)
                        {
                            username = preHost.Substring(lastColon + 1);
                        }
                        else
                        {
                            username = preHost;
                        }
                    }
                }

                Native.Win32CredentialManager.Write(target, username, value);
                return;
            }

            _secrets[key] = value;
            Save();
        }

        public string? GetSecret(string key)
        {
            ReloadIfFileBacked();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string target = key.StartsWith("NovaTerminal:") ? key : $"NovaTerminal:{key}";
                var cred = Native.Win32CredentialManager.Read(target);
                return cred?.Password;
            }

            if (_secrets.TryGetValue(key, out var value))
            {
                return value;
            }
            return null;
        }

        public bool RemoveSecret(string key)
        {
            ReloadIfFileBacked();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string target = key.StartsWith("NovaTerminal:") ? key : $"NovaTerminal:{key}";
                return Native.Win32CredentialManager.Delete(target);
            }

            if (_secrets.Remove(key))
            {
                Save();
                return true;
            }
            return false;
        }

        public IEnumerable<string> ListKeys()
        {
            ReloadIfFileBacked();
            return _secrets.Keys;
        }

        private void ReloadIfFileBacked()
        {
            if (string.IsNullOrEmpty(_vaultPath) || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            _secrets = new Dictionary<string, string>();
            Load();
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(_vaultPath)) return;

            try
            {
                string json = JsonSerializer.Serialize(_secrets, AppJsonContext.Default.DictionaryStringString);
                byte[] data = Encoding.UTF8.GetBytes(json);
                byte[] encrypted;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Encrypt for Current User only (DPAPI)
                    encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                }
                else
                {
                    // Cross-platform fallback: AES-256-GCM
                    encrypted = EncryptFallback(data);
                }

                File.WriteAllBytes(_vaultPath, encrypted);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Vault] Save failed: {ex.Message}");
            }
        }

        private void Load()
        {
            if (string.IsNullOrEmpty(_vaultPath) || !File.Exists(_vaultPath)) return;

            try
            {
                byte[] encrypted = File.ReadAllBytes(_vaultPath);
                byte[] data;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Decrypt (DPAPI)
                    data = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                }
                else
                {
                    // Cross-platform fallback: AES-256-GCM
                    data = DecryptFallback(encrypted);
                }

                string json = Encoding.UTF8.GetString(data);
                var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString);
                if (loaded != null)
                {
                    _secrets = loaded;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Vault] Load failed: {ex.Message}");
            }
        }

        private byte[] EncryptFallback(byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = GetPlatformKey();
            aes.GenerateIV();
            using var encryptor = aes.CreateEncryptor();
            byte[] encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);

            // Prepend IV to the encrypted data
            byte[] result = new byte[aes.IV.Length + encrypted.Length];
            Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
            Buffer.BlockCopy(encrypted, 0, result, aes.IV.Length, encrypted.Length);
            return result;
        }

        private byte[] DecryptFallback(byte[] encryptedWithIv)
        {
            using var aes = Aes.Create();
            aes.Key = GetPlatformKey();
            byte[] iv = new byte[aes.BlockSize / 8];
            byte[] encrypted = new byte[encryptedWithIv.Length - iv.Length];
            Buffer.BlockCopy(encryptedWithIv, 0, iv, 0, iv.Length);
            Buffer.BlockCopy(encryptedWithIv, iv.Length, encrypted, 0, encrypted.Length);

            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        }

        private byte[] GetPlatformKey()
        {
            // Derive a key from machine-specific info
            string machineId = "NovaTerminal-Fallback-Salt";
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (File.Exists("/etc/machine-id")) machineId = File.ReadAllText("/etc/machine-id");
                    else if (File.Exists("/var/lib/dbus/machine-id")) machineId = File.ReadAllText("/var/lib/dbus/machine-id");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    // Basic fallback for macOS key derivation
                    machineId = Environment.GetEnvironmentVariable("USER") ?? "mac-user";
                }
            }
            catch { }

            return Rfc2898DeriveBytes.Pbkdf2(
                machineId,
                Encoding.UTF8.GetBytes("NovaVaultSalt"),
                10000,
                HashAlgorithmName.SHA256,
                32);
        }
    }
}
