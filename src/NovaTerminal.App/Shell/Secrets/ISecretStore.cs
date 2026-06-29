namespace NovaTerminal.Shell.Secrets
{
    /// <summary>
    /// Abstracts per-user secret storage. Implementations back onto OS keychains
    /// (Windows Credential Manager, macOS Keychain, Linux Secret Service) or, for
    /// tests, an in-memory dictionary. There is intentionally no file-based,
    /// machine-derived-key implementation (see issue #100).
    /// </summary>
    public interface ISecretStore
    {
        /// <summary>True when this store can persist secrets right now.</summary>
        bool IsAvailable { get; }

        /// <summary>Returns the stored value, or null if absent.</summary>
        string? Read(string key);

        /// <summary>Creates or overwrites the value for <paramref name="key"/>.</summary>
        void Write(string key, string value);

        /// <summary>Removes the value; returns true if something was removed.</summary>
        bool Delete(string key);
    }
}
