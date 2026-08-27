using System;
using System.Collections.Generic;
using NovaTerminal.Shell.Secrets;

namespace NovaTerminal.Shell
{
    public interface ISshPasswordVault
    {
        void ApplyRememberPasswordPreference(Guid profileId, bool rememberPasswordInVault, string? password = null);
        void ApplyRememberPasswordPreference(TerminalProfile profile, bool rememberPasswordInVault, string? password = null);
    }

    /// <summary>
    /// Read/clear access to the password a profile has saved in the OS credential store.
    /// </summary>
    /// <remarks>
    /// Both members operate on <em>profile-scoped</em> keys only — the canonical
    /// <c>SSH:PROFILE:{guid}</c> key plus this profile's own legacy keys. The shared
    /// <c>SSH:{user}@{host}</c> alias is excluded on purpose: sibling profiles on the same
    /// host resolve through it, so clearing it here would silently break them.
    ///
    /// Consequence: a password stored <em>only</em> under that shared alias reports
    /// <see cref="HasSavedPassword"/> as <see langword="false"/> even though
    /// <see cref="VaultService.ResolveSshPasswordForProfile"/> will still auto-fill from it at
    /// connect time. Such a secret migrates to a profile-scoped key on first use, after which
    /// this reports correctly. That is a legacy-store artifact, and preferable to letting one
    /// profile delete another's password.
    /// </remarks>
    public interface ISavedPasswordAccess
    {
        /// <summary>True when the underlying credential store can be read and written.</summary>
        bool IsVaultAvailable { get; }

        /// <summary>True when a profile-scoped password is stored for <paramref name="profile"/>.</summary>
        bool HasSavedPassword(TerminalProfile profile);

        /// <summary>Clears every profile-scoped password key; true if anything was removed.</summary>
        bool ForgetSavedPassword(TerminalProfile profile);
    }

    public class VaultService
        : ISshPasswordVault, ISavedPasswordAccess
    {
        private readonly ISecretStore _store;

        public VaultService() : this(SecretStore.CreateDefault())
        {
        }

        public VaultService(ISecretStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public bool PersistenceAvailable => _store.IsAvailable;

        public bool IsVaultAvailable => _store.IsAvailable;

        public bool HasSavedPassword(TerminalProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            // GetSecret, not ResolveSshPasswordForProfile: the latter migrates a legacy hit
            // to the canonical key, and this runs on every selection change in the UI.
            foreach (string key in GetProfileScopedSshPasswordKeysForProfile(profile))
            {
                if (!string.IsNullOrEmpty(GetSecret(key)))
                {
                    return true;
                }
            }

            return false;
        }

        public bool ForgetSavedPassword(TerminalProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            bool removedAny = false;
            foreach (string key in GetProfileScopedSshPasswordKeysForProfile(profile))
            {
                removedAny |= RemoveSecret(key);
            }

            return removedAny;
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
                foreach (string key in GetProfileScopedSshPasswordKeysForProfile(profile))
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

        public static IEnumerable<string> GetLegacySshKeys(TerminalProfile profile, bool includeSharedAlias = true)
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

                if (includeSharedAlias)
                {
                    yield return $"SSH:{user}@{host}";
                }
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

        public static IEnumerable<string> GetProfileScopedSshPasswordKeysForProfile(TerminalProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            yield return GetCanonicalSshProfileKey(profile.Id);
            foreach (string legacyKey in GetLegacySshKeys(profile, includeSharedAlias: false))
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
            if (!_store.IsAvailable) return;
            _store.Write(key, value);
        }

        public string? GetSecret(string key)
        {
            if (!_store.IsAvailable) return null;
            return _store.Read(key);
        }

        public bool RemoveSecret(string key)
        {
            if (!_store.IsAvailable) return false;
            return _store.Delete(key);
        }
    }
}
