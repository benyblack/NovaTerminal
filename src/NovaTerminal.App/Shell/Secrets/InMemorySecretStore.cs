using System.Collections.Generic;

namespace NovaTerminal.Shell.Secrets
{
    /// <summary>
    /// Process-local secret store for tests. Not used in production.
    /// </summary>
    public sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new();

        public bool IsAvailable => true;

        public string? Read(string key)
            => _secrets.TryGetValue(key, out string? value) ? value : null;

        public void Write(string key, string value) => _secrets[key] = value;

        public bool Delete(string key) => _secrets.Remove(key);
    }
}
