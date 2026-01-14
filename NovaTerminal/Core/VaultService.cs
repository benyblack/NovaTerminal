using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace NovaTerminal.Core
{
    public class VaultService
    {
        private const string AppName = "NovaTerminal";
        private const string VaultFileName = "vault.dat";
        private readonly string _vaultPath;
        private Dictionary<string, string> _secrets;
        
#pragma warning disable CA1416 // Validate platform compatibility

        public VaultService()
        {
            try
            {
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

        public void SetSecret(string key, string value)
        {
            _secrets[key] = value;
            Save();
        }

        public string? GetSecret(string key)
        {
            if (_secrets.TryGetValue(key, out var value))
            {
                return value;
            }
            return null;
        }

        public bool RemoveSecret(string key)
        {
            if (_secrets.Remove(key))
            {
                Save();
                return true;
            }
            return false;
        }

        public IEnumerable<string> ListKeys()
        {
            return _secrets.Keys;
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(_vaultPath)) return;

            try
            {
                string json = JsonConvert.SerializeObject(_secrets);
                byte[] data = Encoding.UTF8.GetBytes(json);
                
                // Encrypt for Current User only
                byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                
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
                
                // Decrypt
                byte[] data = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(data);
                
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (loaded != null)
                {
                    _secrets = loaded;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Vault] Load failed: {ex.Message}");
                // If load fails (e.g. invalid data), we start with empty to avoid crashing
            }
        }
    }
}
