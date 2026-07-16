# Vault Keychain Security Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the trivially-derivable machine-id vault key on Linux/macOS with OS-keychain storage (P/Invoke), surface init failures, and delete the legacy weak-encryption code and files (#100).

**Architecture:** Extract storage behind an `ISecretStore` interface. `VaultService` keeps its policy methods and delegates Read/Write/Delete to an injected store. Platform stores: Windows Credential Manager (existing logic, wrapped), macOS Keychain (`Security.framework`), Linux Secret Service (`libsecret`). When no store is available, the vault runs in persistence-disabled mode (no-op writes, null reads) and surfaces a one-time banner. The weak AES/PBKDF2 file path is removed entirely.

**Tech Stack:** C# / .NET 10, Avalonia, `LibraryImport`/`DllImport` P/Invoke, xUnit. Build/test via `scripts/build.ps1` (Windows) / `scripts/build.sh` (Linux/macOS).

**Spec:** `docs/superpowers/specs/2026-06-11-vault-keychain-security-design.md`

**Build/test note (from CLAUDE.md):** ALWAYS use the wrapper scripts, never raw `dotnet`. Test the affected project only for speed:
`scripts/build.ps1 test tests/NovaTerminal.App.Tests` (Windows) /
`scripts/build.sh test tests/NovaTerminal.App.Tests` (Linux/macOS).

**Native-validation caveat:** The Linux/macOS P/Invoke (Tasks 4–5) cannot be exercised on Windows, and CI Linux runners usually have no unlocked D-Bus keyring, so the keychain integration tests (Task 6) will *skip* there. The native paths MUST be validated manually on a real macOS machine and a Linux desktop session (or VM with GNOME Keyring) before the PR is considered done — see Task 8.

---

## File Structure

**Create:**
- `src/NovaTerminal.App/Shell/Secrets/ISecretStore.cs` — storage interface.
- `src/NovaTerminal.App/Shell/Secrets/InMemorySecretStore.cs` — test/in-memory store.
- `src/NovaTerminal.App/Shell/Secrets/WindowsCredentialStore.cs` — wraps `Win32CredentialManager`.
- `src/NovaTerminal.App/Shell/Secrets/LinuxSecretStore.cs` — libsecret P/Invoke.
- `src/NovaTerminal.App/Shell/Secrets/MacKeychainStore.cs` — Security.framework P/Invoke.
- `src/NovaTerminal.App/Shell/Secrets/SecretStore.cs` — `CreateDefault()` factory + legacy cleanup.
- `tests/NovaTerminal.App.Tests/Core/InMemorySecretStoreTests.cs`
- `tests/NovaTerminal.App.Tests/Core/VaultServiceDisabledModeTests.cs`
- `tests/NovaTerminal.App.Tests/Core/KeychainSecretStoreIntegrationTests.cs`

**Modify:**
- `src/NovaTerminal.App/Shell/VaultService.cs` — delegate to `ISecretStore`; delete file crypto.
- `src/NovaTerminal.App/Shell/AppPaths.cs` — add `LegacyVaultFilePath`.
- `src/NovaTerminal.App/MainWindow.axaml.cs:2354` — surface init failure; share instance.
- `src/NovaTerminal.App/MainWindow.axaml.cs:5278` — `ShowRecordingToast` hides folder button when no folder.
- `src/NovaTerminal.App/Services/Ssh/RemoteDirectoryBrowserService.cs:37`
- `src/NovaTerminal.App/Services/Ssh/RemotePathAutocompleteService.cs:33`
- `src/NovaTerminal.App/Shell/SftpService.cs:596`
- `tests/NovaTerminal.App.Tests/Core/VaultServiceSshKeyTests.cs` — migrate file-backed tests.
- `tests/NovaTerminal.App.Tests/Ssh/SshInteractionServiceTests.cs` — migrate 10 file-backed sites.

**Unchanged (intentional):** `src/NovaTerminal.App/Shell/SshAskPassCommand.cs` keeps `new VaultService()` (separate helper process; reads keychain written by main app).

---

## Task 1: `ISecretStore` interface + `InMemorySecretStore`

**Files:**
- Create: `src/NovaTerminal.App/Shell/Secrets/ISecretStore.cs`
- Create: `src/NovaTerminal.App/Shell/Secrets/InMemorySecretStore.cs`
- Test: `tests/NovaTerminal.App.Tests/Core/InMemorySecretStoreTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/NovaTerminal.App.Tests/Core/InMemorySecretStoreTests.cs`:

```csharp
using NovaTerminal.Shell.Secrets;

namespace NovaTerminal.Tests.Core;

public class InMemorySecretStoreTests
{
    [Fact]
    public void IsAvailable_IsTrue()
    {
        var store = new InMemorySecretStore();
        Assert.True(store.IsAvailable);
    }

    [Fact]
    public void Write_ThenRead_ReturnsValue()
    {
        var store = new InMemorySecretStore();
        store.Write("k", "v");
        Assert.Equal("v", store.Read("k"));
    }

    [Fact]
    public void Read_MissingKey_ReturnsNull()
    {
        var store = new InMemorySecretStore();
        Assert.Null(store.Read("missing"));
    }

    [Fact]
    public void Write_OverwritesExistingValue()
    {
        var store = new InMemorySecretStore();
        store.Write("k", "v1");
        store.Write("k", "v2");
        Assert.Equal("v2", store.Read("k"));
    }

    [Fact]
    public void Delete_ExistingKey_ReturnsTrueAndRemoves()
    {
        var store = new InMemorySecretStore();
        store.Write("k", "v");
        Assert.True(store.Delete("k"));
        Assert.Null(store.Read("k"));
    }

    [Fact]
    public void Delete_MissingKey_ReturnsFalse()
    {
        var store = new InMemorySecretStore();
        Assert.False(store.Delete("missing"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter InMemorySecretStoreTests`
Expected: FAIL to compile — `ISecretStore`/`InMemorySecretStore` don't exist.

- [ ] **Step 3: Write the interface**

Create `src/NovaTerminal.App/Shell/Secrets/ISecretStore.cs`:

```csharp
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
```

- [ ] **Step 4: Write the in-memory implementation**

Create `src/NovaTerminal.App/Shell/Secrets/InMemorySecretStore.cs`:

```csharp
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
```

- [ ] **Step 5: Run test to verify it passes**

Run: `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter InMemorySecretStoreTests`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Shell/Secrets/ISecretStore.cs src/NovaTerminal.App/Shell/Secrets/InMemorySecretStore.cs tests/NovaTerminal.App.Tests/Core/InMemorySecretStoreTests.cs
git commit -m "feat: add ISecretStore abstraction with in-memory store (#100)"
```

---

## Task 2: Refactor `VaultService` onto `ISecretStore` + `WindowsCredentialStore`

This task removes the weak file crypto and moves Windows credential logic into a store. The existing static policy methods stay untouched.

**Files:**
- Create: `src/NovaTerminal.App/Shell/Secrets/WindowsCredentialStore.cs`
- Modify: `src/NovaTerminal.App/Shell/VaultService.cs`
- Test: `tests/NovaTerminal.App.Tests/Core/VaultServiceDisabledModeTests.cs`
- Migrate: `tests/NovaTerminal.App.Tests/Core/VaultServiceSshKeyTests.cs`, `tests/NovaTerminal.App.Tests/Ssh/SshInteractionServiceTests.cs`

- [ ] **Step 1: Write the failing disabled-mode test**

Create `tests/NovaTerminal.App.Tests/Core/VaultServiceDisabledModeTests.cs`:

```csharp
using NovaTerminal.Shell;
using NovaTerminal.Shell.Secrets;

namespace NovaTerminal.Tests.Core;

public class VaultServiceDisabledModeTests
{
    private sealed class UnavailableStore : ISecretStore
    {
        public bool IsAvailable => false;
        public string? Read(string key) => "should-not-be-read";
        public void Write(string key, string value) => throw new InvalidOperationException("must not write");
        public bool Delete(string key) => throw new InvalidOperationException("must not delete");
    }

    [Fact]
    public void PersistenceAvailable_ReflectsStore()
    {
        Assert.False(new VaultService(new UnavailableStore()).PersistenceAvailable);
        Assert.True(new VaultService(new InMemorySecretStore()).PersistenceAvailable);
    }

    [Fact]
    public void GetSecret_WhenUnavailable_ReturnsNullWithoutReadingStore()
    {
        var vault = new VaultService(new UnavailableStore());
        Assert.Null(vault.GetSecret("any"));
    }

    [Fact]
    public void SetSecret_WhenUnavailable_IsNoOpAndDoesNotThrow()
    {
        var vault = new VaultService(new UnavailableStore());
        vault.SetSecret("k", "v"); // must not throw
    }

    [Fact]
    public void RemoveSecret_WhenUnavailable_ReturnsFalseWithoutThrowing()
    {
        var vault = new VaultService(new UnavailableStore());
        Assert.False(vault.RemoveSecret("k"));
    }

    [Fact]
    public void RoundTrip_ThroughInjectedStore_Works()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);
        vault.SetSecret("k", "v");
        Assert.Equal("v", vault.GetSecret("k"));
        Assert.True(vault.RemoveSecret("k"));
        Assert.Null(vault.GetSecret("k"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter VaultServiceDisabledModeTests`
Expected: FAIL to compile — `VaultService(ISecretStore)` ctor and `PersistenceAvailable` don't exist.

- [ ] **Step 3: Create `WindowsCredentialStore`**

Move the Windows Credential Manager key-mapping logic out of `VaultService` into a store. Create `src/NovaTerminal.App/Shell/Secrets/WindowsCredentialStore.cs`:

```csharp
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
```

- [ ] **Step 4: Rewrite `VaultService` to delegate to `ISecretStore`**

Replace the entire body of `src/NovaTerminal.App/Shell/VaultService.cs` with the version below. All static policy methods (`GetCanonicalSshProfileKey`, `ApplyRememberPasswordPreference` overloads, `GetLegacySshKeys`, `GetSshPasswordKeysForProfile`, `GetProfileScopedSshPasswordKeysForProfile`, `ResolveSshPasswordForProfile`) are **unchanged** — copy them verbatim from the current file. Only the constructor, fields, and the three instance Read/Write/Delete methods change. The `using System.Security.Cryptography;`, `System.Text;`, `System.Text.Json;` imports and `GetPlatformKey`/`EncryptFallback`/`DecryptFallback`/`Save`/`TryLoadSecrets`/`LoadSecretsOrEmpty`/`ReloadIfFileBacked`/`ListKeys`/`UsesWindowsCredentialManager` members are **deleted**.

The new top-of-class and instance methods:

```csharp
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

    public class VaultService : ISshPasswordVault
    {
        private readonly ISecretStore _store;

        /// <summary>Production constructor: selects the platform keychain store.</summary>
        public VaultService() : this(SecretStore.CreateDefault())
        {
        }

        /// <summary>Test/DI constructor: inject any secret store.</summary>
        public VaultService(ISecretStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>True when secrets can be persisted. When false, the vault is read-as-empty
        /// and writes are silently ignored so SSH connect paths keep working.</summary>
        public bool PersistenceAvailable => _store.IsAvailable;

        // ----- static policy methods: COPY VERBATIM from existing file -----
        // GetCanonicalSshProfileKey, ApplyRememberPasswordPreference (both static overloads),
        // GetLegacySshKeys, GetSshPasswordKeysForProfile, GetProfileScopedSshPasswordKeysForProfile,
        // ResolveSshPasswordForProfile, and the two instance ApplyRememberPasswordPreference
        // wrappers, GetSshPasswordForProfile, SetSshPasswordForProfile.

        public void SetSecret(string key, string value)
        {
            if (!_store.IsAvailable)
            {
                return;
            }

            _store.Write(key, value);
        }

        public string? GetSecret(string key)
        {
            if (!_store.IsAvailable)
            {
                return null;
            }

            return _store.Read(key);
        }

        public bool RemoveSecret(string key)
        {
            if (!_store.IsAvailable)
            {
                return false;
            }

            return _store.Delete(key);
        }
    }
}
```

> NOTE: `SecretStore.CreateDefault()` is created in Task 3. Until then, the production `VaultService()` ctor won't compile. To keep this task green in isolation, temporarily stub it: add `src/NovaTerminal.App/Shell/Secrets/SecretStore.cs` with `public static ISecretStore CreateDefault() => new InMemorySecretStore();` and replace it properly in Task 3. (The disabled-mode tests use the `ISecretStore` ctor and don't depend on the factory.)

- [ ] **Step 5: Migrate `VaultServiceSshKeyTests.cs`**

In `tests/NovaTerminal.App.Tests/Core/VaultServiceSshKeyTests.cs`:

1. **Replace** `FileBackedVaultInstances_SeeAndRemoveEachOthersSecrets` (lines ~109–134) with a shared-store version:

```csharp
[Fact]
public void VaultInstances_SharingAStore_SeeAndRemoveEachOthersSecrets()
{
    var store = new NovaTerminal.Shell.Secrets.InMemorySecretStore();
    TerminalProfile profile = CreateProfile();

    var writer = new VaultService(store);
    var remover = new VaultService(store);

    writer.SetSshPasswordForProfile(profile, "shared-secret");

    Assert.Equal("shared-secret", remover.GetSshPasswordForProfile(profile));

    remover.ApplyRememberPasswordPreference(profile, rememberPasswordInVault: false);

    Assert.Null(writer.GetSshPasswordForProfile(profile));
    Assert.Null(remover.GetSshPasswordForProfile(profile));
}
```

2. **Delete** `FileBackedReload_PreservesCachedSecrets_WhenLoadFails` (lines ~136–158) entirely — it tested the file-reload-on-corruption behavior that no longer exists.

3. If `CreateTempDirectory()` is now unused, delete it. Add `using NovaTerminal.Shell.Secrets;` if you prefer the unqualified name.

- [ ] **Step 6: Migrate `SshInteractionServiceTests.cs`**

In `tests/NovaTerminal.App.Tests/Ssh/SshInteractionServiceTests.cs`, for each of the 10 occurrences, replace this pattern:

```csharp
string tempRoot = CreateTempDirectory();
try
{
    string vaultPath = Path.Combine(tempRoot, "vault.dat");
    var vault = new VaultService(vaultPath);
    ...
}
finally
{
    Directory.Delete(tempRoot, recursive: true);
}
```

with:

```csharp
var vault = new VaultService(new NovaTerminal.Shell.Secrets.InMemorySecretStore());
...
```

(Remove the surrounding `tempRoot`/try-finally scaffolding for each test. If `CreateTempDirectory` becomes unused in this file, delete it.)

- [ ] **Step 7: Run the affected tests**

Run: `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter "VaultServiceDisabledModeTests|VaultServiceSshKeyTests|SshInteractionServiceTests"`
Expected: PASS. On Windows the production `VaultService()` is not exercised by these tests (they inject stores), so the temporary `CreateDefault` stub is fine.

- [ ] **Step 8: Commit**

```bash
git add src/NovaTerminal.App/Shell/VaultService.cs src/NovaTerminal.App/Shell/Secrets/WindowsCredentialStore.cs src/NovaTerminal.App/Shell/Secrets/SecretStore.cs tests/NovaTerminal.App.Tests/Core/VaultServiceDisabledModeTests.cs tests/NovaTerminal.App.Tests/Core/VaultServiceSshKeyTests.cs tests/NovaTerminal.App.Tests/Ssh/SshInteractionServiceTests.cs
git commit -m "refactor: VaultService delegates to ISecretStore; drop weak file crypto (#100)"
```

---

## Task 3: `SecretStore.CreateDefault()` factory + legacy `vault.dat` deletion

**Files:**
- Modify: `src/NovaTerminal.App/Shell/Secrets/SecretStore.cs` (replace the Task 2 stub)
- Modify: `src/NovaTerminal.App/Shell/AppPaths.cs`
- Test: `tests/NovaTerminal.App.Tests/Core/VaultServiceDisabledModeTests.cs` (add legacy-cleanup test)

- [ ] **Step 1: Add `LegacyVaultFilePath` to `AppPaths`**

In `src/NovaTerminal.App/Shell/AppPaths.cs`, after the `NativeKnownHostsFilePath` property (line 53), add:

```csharp
        /// <summary>Path of the pre-#100 weakly-encrypted vault file, kept only so it can be deleted.</summary>
        public static string LegacyVaultFilePath => Path.Combine(RootDirectory, "vault.dat");
```

- [ ] **Step 2: Write the failing legacy-cleanup test**

Add to `tests/NovaTerminal.App.Tests/Core/VaultServiceDisabledModeTests.cs`:

```csharp
    [Fact]
    public void DeleteLegacyVaultFile_RemovesFile_WhenPresent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nova-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string legacy = Path.Combine(dir, "vault.dat");
        File.WriteAllBytes(legacy, new byte[] { 1, 2, 3 });
        try
        {
            NovaTerminal.Shell.Secrets.SecretStore.DeleteLegacyVaultFile(legacy);
            Assert.False(File.Exists(legacy));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DeleteLegacyVaultFile_DoesNotThrow_WhenAbsent()
    {
        NovaTerminal.Shell.Secrets.SecretStore.DeleteLegacyVaultFile(
            Path.Combine(Path.GetTempPath(), "nova-missing-" + Guid.NewGuid().ToString("N"), "vault.dat"));
    }
```

Add `using System;` and `using System.IO;` at the top of the file if not already present.

- [ ] **Step 3: Run test to verify it fails**

Run: `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter VaultServiceDisabledModeTests`
Expected: FAIL to compile — `SecretStore.DeleteLegacyVaultFile` doesn't exist.

- [ ] **Step 4: Implement the factory + cleanup**

Replace `src/NovaTerminal.App/Shell/Secrets/SecretStore.cs` (the Task 2 stub) with:

```csharp
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
```

> `MacKeychainStore` and `LinuxSecretStore` are created in Tasks 5 and 4. This file will not compile until those types exist. Implement Task 4 next; if you need this task green in isolation first, temporarily `return new InMemorySecretStore();` in both non-Windows branches and restore in Task 4/5.

- [ ] **Step 5: Run test (Windows path + cleanup)**

Run: `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter VaultServiceDisabledModeTests`
Expected: PASS once `LinuxSecretStore`/`MacKeychainStore` exist (Tasks 4–5) or the temporary stub is in place.

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Shell/Secrets/SecretStore.cs src/NovaTerminal.App/Shell/AppPaths.cs tests/NovaTerminal.App.Tests/Core/VaultServiceDisabledModeTests.cs
git commit -m "feat: platform secret-store factory + legacy vault.dat deletion (#100)"
```

---

## Task 4: `LinuxSecretStore` (libsecret P/Invoke)

**REQUIRED SUB-SKILL:** Invoke `dotnet:dotnet-pinvoke` before writing the bindings — it covers string marshalling, `SafeHandle`, and lifetime rules used below.

Uses the non-varargs ("vectored") libsecret API with a single-attribute `SecretSchema` and a glib `GHashTable` so we avoid varargs marshalling. Items are namespaced by schema name `com.novaterminal.Vault` and distinguished by a `"key"` attribute.

**Files:**
- Create: `src/NovaTerminal.App/Shell/Secrets/LinuxSecretStore.cs`

- [ ] **Step 1: Implement the store**

Create `src/NovaTerminal.App/Shell/Secrets/LinuxSecretStore.cs`:

```csharp
using System;
using System.Runtime.InteropServices;

namespace NovaTerminal.Shell.Secrets
{
    /// <summary>
    /// Linux secret store backed by libsecret / Secret Service (GNOME Keyring, KWallet).
    /// Secrets are protected by the user's login keyring, not by any machine-derived key.
    /// </summary>
    public sealed class LinuxSecretStore : ISecretStore
    {
        private const string Lib = "libsecret-1.so.0";
        private const string Glib = "libglib-2.0.so.0";
        private const string SchemaName = "com.novaterminal.Vault";
        private const string KeyAttribute = "key";

        // SecretSchemaAttributeType.SECRET_SCHEMA_ATTRIBUTE_STRING = 0
        // SecretSchemaFlags.SECRET_SCHEMA_NONE = 0
        private readonly IntPtr _schema;
        private readonly bool _available;

        public LinuxSecretStore()
        {
            try
            {
                _schema = BuildSchema();
                // Probe: a lookup that returns NULL with no GError means the service is reachable.
                _ = LookupRaw("__novaterminal_probe__", out bool serviceError);
                _available = !serviceError;
            }
            catch (DllNotFoundException) { _available = false; }
            catch (EntryPointNotFoundException) { _available = false; }
        }

        public bool IsAvailable => _available;

        public string? Read(string key)
        {
            if (!_available) return null;
            return LookupRaw(key, out _);
        }

        public void Write(string key, string value)
        {
            if (!_available) return;
            IntPtr attrs = BuildAttributes(key);
            try
            {
                secret_password_storev_sync(
                    _schema, attrs, IntPtr.Zero /* default collection */,
                    label: $"NovaTerminal: {key}", password: value,
                    cancellable: IntPtr.Zero, error: out IntPtr error);
                FreeError(error);
            }
            finally
            {
                g_hash_table_destroy(attrs);
            }
        }

        public bool Delete(string key)
        {
            if (!_available) return false;
            IntPtr attrs = BuildAttributes(key);
            try
            {
                bool removed = secret_password_clearv_sync(_schema, attrs, IntPtr.Zero, out IntPtr error);
                FreeError(error);
                return removed;
            }
            finally
            {
                g_hash_table_destroy(attrs);
            }
        }

        private string? LookupRaw(string key, out bool serviceError)
        {
            IntPtr attrs = BuildAttributes(key);
            try
            {
                IntPtr result = secret_password_lookupv_sync(_schema, attrs, IntPtr.Zero, out IntPtr error);
                serviceError = error != IntPtr.Zero;
                FreeError(error);
                if (result == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUTF8(result); }
                finally { secret_password_free(result); }
            }
            finally
            {
                g_hash_table_destroy(attrs);
            }
        }

        // Builds a GHashTable<string,string> containing { "key": <value> }.
        private static IntPtr BuildAttributes(string key)
        {
            IntPtr table = g_hash_table_new(g_str_hash, g_str_equal);
            // Keys/values are borrowed by libsecret only for the call duration.
            IntPtr namePtr = Marshal.StringToCoTaskMemUTF8(KeyAttribute);
            IntPtr valuePtr = Marshal.StringToCoTaskMemUTF8(key);
            g_hash_table_insert(table, namePtr, valuePtr);
            return table;
            // NOTE: namePtr/valuePtr are leaked per call. Acceptable for low-frequency vault ops;
            // if churn matters, switch to a GDestroyNotify-backed table. Validate with the
            // dotnet-pinvoke skill during implementation.
        }

        // Allocates a SecretSchema with one STRING attribute named "key", terminated by a NULL name.
        private static IntPtr BuildSchema()
        {
            // struct SecretSchema { const char* name; int flags;
            //   struct { const char* name; int type; } attributes[32]; }
            int attrEntry = IntPtr.Size + sizeof(int);          // {name, type} with padding
            int headerSize = IntPtr.Size + sizeof(int);          // name + flags
            // Round header up to pointer alignment for the attributes array.
            int headerPadded = (headerSize + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
            int size = headerPadded + attrEntry * 32;
            IntPtr schema = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(schema, i, 0);

            Marshal.WriteIntPtr(schema, 0, Marshal.StringToHGlobalAnsi(SchemaName));
            Marshal.WriteInt32(schema, IntPtr.Size, 0); // SECRET_SCHEMA_NONE

            int off = headerPadded;
            Marshal.WriteIntPtr(schema, off, Marshal.StringToHGlobalAnsi(KeyAttribute));
            Marshal.WriteInt32(schema, off + IntPtr.Size, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
            // Remaining 31 entries already zeroed -> terminator.
            return schema;
        }

        private static void FreeError(IntPtr error)
        {
            if (error != IntPtr.Zero) g_error_free(error);
        }

        // ---- libsecret ----
        [DllImport(Lib)]
        private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        [DllImport(Lib)]
        private static extern bool secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
            IntPtr cancellable, out IntPtr error);

        [DllImport(Lib)]
        private static extern bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        [DllImport(Lib)]
        private static extern void secret_password_free(IntPtr password);

        // ---- glib ----
        [DllImport(Glib)]
        private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr keyEqualFunc);
        [DllImport(Glib)]
        private static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
        [DllImport(Glib)]
        private static extern void g_hash_table_destroy(IntPtr table);
        [DllImport(Glib)]
        private static extern void g_error_free(IntPtr error);

        // g_str_hash / g_str_equal are exported function symbols; take their addresses via dlsym-like import.
        private static readonly IntPtr g_str_hash = NativeLibrary.GetExport(NativeLibrary.Load(Glib), "g_str_hash");
        private static readonly IntPtr g_str_equal = NativeLibrary.GetExport(NativeLibrary.Load(Glib), "g_str_equal");
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: SUCCESS (compiles on Windows even though the lib only loads on Linux). Restore any temporary stub from Task 3 step 4.

- [ ] **Step 3: Commit**

```bash
git add src/NovaTerminal.App/Shell/Secrets/LinuxSecretStore.cs src/NovaTerminal.App/Shell/Secrets/SecretStore.cs
git commit -m "feat: LinuxSecretStore via libsecret P/Invoke (#100)"
```

---

## Task 5: `MacKeychainStore` (Security.framework P/Invoke)

**REQUIRED SUB-SKILL:** Invoke `dotnet:dotnet-pinvoke` before writing the bindings.

Uses Keychain Services generic-password items: `kSecAttrService = "NovaTerminal"`, `kSecAttrAccount = key`. CoreFoundation objects created here are released with `CFRelease`.

**Files:**
- Create: `src/NovaTerminal.App/Shell/Secrets/MacKeychainStore.cs`

- [ ] **Step 1: Implement the store**

Create `src/NovaTerminal.App/Shell/Secrets/MacKeychainStore.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NovaTerminal.Shell.Secrets
{
    /// <summary>
    /// macOS secret store backed by Keychain Services (login keychain, per-user).
    /// </summary>
    public sealed class MacKeychainStore : ISecretStore
    {
        private const string Sec = "/System/Library/Frameworks/Security.framework/Security";
        private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string ServiceName = "NovaTerminal";

        private const int errSecSuccess = 0;
        private const int errSecItemNotFound = -25300;
        private const int errSecDuplicateItem = -25299;
        private const uint kCFStringEncodingUTF8 = 0x08000100;

        private readonly bool _available;

        public MacKeychainStore()
        {
            try
            {
                // Touch the framework so a missing dylib trips DllNotFoundException here.
                _ = Read("__novaterminal_probe__");
                _available = true;
            }
            catch (DllNotFoundException) { _available = false; }
            catch (EntryPointNotFoundException) { _available = false; }
        }

        public bool IsAvailable => _available;

        public string? Read(string key)
        {
            IntPtr query = BuildQuery(key, forReturnData: true);
            try
            {
                int status = SecItemCopyMatching(query, out IntPtr dataRef);
                if (status == errSecItemNotFound) return null;
                if (status != errSecSuccess || dataRef == IntPtr.Zero) return null;
                try
                {
                    IntPtr bytes = CFDataGetBytePtr(dataRef);
                    long len = CFDataGetLength(dataRef);
                    if (bytes == IntPtr.Zero || len <= 0) return string.Empty;
                    var buffer = new byte[len];
                    Marshal.Copy(bytes, buffer, 0, (int)len);
                    return Encoding.UTF8.GetString(buffer);
                }
                finally { CFRelease(dataRef); }
            }
            finally { CFRelease(query); }
        }

        public void Write(string key, string value)
        {
            byte[] valueBytes = Encoding.UTF8.GetBytes(value);

            // Try update first; if the item is absent, add it.
            IntPtr matchQuery = BuildQuery(key, forReturnData: false);
            IntPtr attrsToUpdate = CFDictionaryCreateMutable();
            IntPtr valueData = CFDataCreate(IntPtr.Zero, valueBytes, valueBytes.Length);
            try
            {
                CFDictionaryAddValue(attrsToUpdate, kSecValueData, valueData);
                int status = SecItemUpdate(matchQuery, attrsToUpdate);
                if (status == errSecItemNotFound)
                {
                    IntPtr addQuery = BuildQuery(key, forReturnData: false);
                    try
                    {
                        CFDictionaryAddValue(addQuery, kSecValueData, valueData);
                        SecItemAdd(addQuery, IntPtr.Zero);
                    }
                    finally { CFRelease(addQuery); }
                }
            }
            finally
            {
                CFRelease(valueData);
                CFRelease(attrsToUpdate);
                CFRelease(matchQuery);
            }
        }

        public bool Delete(string key)
        {
            IntPtr query = BuildQuery(key, forReturnData: false);
            try
            {
                int status = SecItemDelete(query);
                return status == errSecSuccess;
            }
            finally { CFRelease(query); }
        }

        // Builds { class: GenericPassword, service: "NovaTerminal", account: key, [returnData/matchLimit] }.
        private static IntPtr BuildQuery(string key, bool forReturnData)
        {
            IntPtr dict = CFDictionaryCreateMutable();
            CFDictionaryAddValue(dict, kSecClass, kSecClassGenericPassword);
            IntPtr service = CFStr(ServiceName);
            IntPtr account = CFStr(key);
            CFDictionaryAddValue(dict, kSecAttrService, service);
            CFDictionaryAddValue(dict, kSecAttrAccount, account);
            CFRelease(service);
            CFRelease(account);
            if (forReturnData)
            {
                CFDictionaryAddValue(dict, kSecReturnData, kCFBooleanTrue);
                CFDictionaryAddValue(dict, kSecMatchLimit, kSecMatchLimitOne);
            }
            return dict;
        }

        private static IntPtr CFStr(string s)
            => CFStringCreateWithCString(IntPtr.Zero, s, kCFStringEncodingUTF8);

        private static IntPtr CFDictionaryCreateMutable()
            => CFDictionaryCreateMutable(IntPtr.Zero, 0, kCFTypeDictionaryKeyCallBacks, kCFTypeDictionaryValueCallBacks);

        // ---- Security.framework ----
        [DllImport(Sec)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
        [DllImport(Sec)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
        [DllImport(Sec)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);
        [DllImport(Sec)] private static extern int SecItemDelete(IntPtr query);

        // ---- CoreFoundation ----
        [DllImport(CF)] private static extern void CFRelease(IntPtr cf);
        [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);
        [DllImport(CF)] private static extern IntPtr CFDataCreate(IntPtr alloc, byte[] bytes, long length);
        [DllImport(CF)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CF)] private static extern long CFDataGetLength(IntPtr data);
        [DllImport(CF)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr alloc, long capacity,
            IntPtr keyCallBacks, IntPtr valueCallBacks);
        [DllImport(CF)] private static extern void CFDictionaryAddValue(IntPtr dict, IntPtr key, IntPtr value);

        // ---- Global symbol constants (resolved from the frameworks at load) ----
        private static readonly IntPtr SecHandle = NativeLibrary.Load(Sec);
        private static readonly IntPtr CFHandle = NativeLibrary.Load(CF);
        private static IntPtr Const(IntPtr handle, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(handle, name));

        private static readonly IntPtr kSecClass = Const(SecHandle, "kSecClass");
        private static readonly IntPtr kSecClassGenericPassword = Const(SecHandle, "kSecClassGenericPassword");
        private static readonly IntPtr kSecAttrService = Const(SecHandle, "kSecAttrService");
        private static readonly IntPtr kSecAttrAccount = Const(SecHandle, "kSecAttrAccount");
        private static readonly IntPtr kSecValueData = Const(SecHandle, "kSecValueData");
        private static readonly IntPtr kSecReturnData = Const(SecHandle, "kSecReturnData");
        private static readonly IntPtr kSecMatchLimit = Const(SecHandle, "kSecMatchLimit");
        private static readonly IntPtr kSecMatchLimitOne = Const(SecHandle, "kSecMatchLimitOne");
        private static readonly IntPtr kCFBooleanTrue = Const(CFHandle, "kCFBooleanTrue");
        private static readonly IntPtr kCFTypeDictionaryKeyCallBacks = NativeLibrary.GetExport(CFHandle, "kCFTypeDictionaryKeyCallBacks");
        private static readonly IntPtr kCFTypeDictionaryValueCallBacks = NativeLibrary.GetExport(CFHandle, "kCFTypeDictionaryValueCallBacks");
    }
}
```

> The `Const`/`GetExport` distinction matters: `kSec*` and `kCFBooleanTrue` are pointer-typed constants (read the pointer at the symbol), while `kCFTypeDictionary*CallBacks` are structs (use the symbol address directly). Confirm with the dotnet-pinvoke skill.

- [ ] **Step 2: Build to verify it compiles**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: SUCCESS on Windows. Restore the Task 3 factory to `new MacKeychainStore()` if stubbed.

- [ ] **Step 3: Commit**

```bash
git add src/NovaTerminal.App/Shell/Secrets/MacKeychainStore.cs src/NovaTerminal.App/Shell/Secrets/SecretStore.cs
git commit -m "feat: MacKeychainStore via Security.framework P/Invoke (#100)"
```

---

## Task 6: Keychain integration tests (skippable)

These exercise the real platform store and **skip** when no keyring is available, so they're safe on Windows/CI.

**Files:**
- Create: `tests/NovaTerminal.App.Tests/Core/KeychainSecretStoreIntegrationTests.cs`

- [ ] **Step 1: Write the tests**

Create `tests/NovaTerminal.App.Tests/Core/KeychainSecretStoreIntegrationTests.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using NovaTerminal.Shell.Secrets;

namespace NovaTerminal.Tests.Core;

public class KeychainSecretStoreIntegrationTests
{
    private static ISecretStore? CreatePlatformStore()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacKeychainStore();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new LinuxSecretStore();
        return null; // Windows uses WindowsCredentialStore, covered elsewhere.
    }

    [Fact]
    public void RoundTrip_WhenKeychainAvailable()
    {
        ISecretStore? store = CreatePlatformStore();
        if (store is null || !store.IsAvailable)
        {
            return; // Skip: no platform keyring in this environment.
        }

        string key = "SSH:PROFILE:" + Guid.NewGuid().ToString("D");
        try
        {
            store.Write(key, "integration-secret");
            Assert.Equal("integration-secret", store.Read(key));

            store.Write(key, "updated-secret");
            Assert.Equal("updated-secret", store.Read(key));

            Assert.True(store.Delete(key));
            Assert.Null(store.Read(key));
        }
        finally
        {
            store.Delete(key);
        }
    }
}
```

- [ ] **Step 2: Run (will skip on Windows)**

Run: `scripts/build.ps1 test tests/NovaTerminal.App.Tests --filter KeychainSecretStoreIntegrationTests`
Expected: PASS (the single test returns early/skips on Windows; it does a real round-trip on macOS/Linux with a keyring).

- [ ] **Step 3: Commit**

```bash
git add tests/NovaTerminal.App.Tests/Core/KeychainSecretStoreIntegrationTests.cs
git commit -m "test: skippable keychain integration round-trip (#100)"
```

---

## Task 7: MainWindow surfacing + single shared instance

**Files:**
- Modify: `src/NovaTerminal.App/MainWindow.axaml.cs` (lines ~2354 and ~5278)
- Modify: `src/NovaTerminal.App/Services/Ssh/RemoteDirectoryBrowserService.cs:37`
- Modify: `src/NovaTerminal.App/Services/Ssh/RemotePathAutocompleteService.cs:33`
- Modify: `src/NovaTerminal.App/Shell/SftpService.cs:596`

- [ ] **Step 1: Surface vault init failure in MainWindow**

In `src/NovaTerminal.App/MainWindow.axaml.cs`, replace line 2354:

```csharp
            try { Vault = new VaultService(); } catch { }
```

with:

```csharp
            try
            {
                Vault = new VaultService();
                if (!Vault.PersistenceAvailable)
                {
                    ShowRecordingToast(
                        "Credential storage unavailable",
                        "No system keychain was found, so SSH passwords won't be saved this session.",
                        filePath: null,
                        folderPath: null,
                        autoHide: true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Vault] Init failed: {ex.Message}");
            }
```

- [ ] **Step 2: Hide the folder button when there's no folder**

So the credential toast doesn't show an irrelevant "Open Folder" action, update `ShowRecordingToast` in `src/NovaTerminal.App/MainWindow.axaml.cs` (~line 5290). After `messageBlock.Text = message;` add:

```csharp
            var openFolderButton = this.FindControl<Button>("RecordingToastOpenFolder");
            if (openFolderButton != null)
            {
                openFolderButton.IsVisible = !string.IsNullOrWhiteSpace(folderPath);
            }
```

- [ ] **Step 3: Route call sites through the shared `MainWindow.Vault`**

In each of the three service files, the constructor currently defaults to `new VaultService()`. Change the fallback to prefer the shared instance, falling back to a fresh one only if the window hasn't constructed it yet.

`src/NovaTerminal.App/Services/Ssh/RemoteDirectoryBrowserService.cs:37` — change:

```csharp
        _passwordResolver = passwordResolver ?? (profile => new VaultService().GetSshPasswordForProfile(profile));
```

to:

```csharp
        _passwordResolver = passwordResolver ?? (profile => (MainWindow.Vault ?? new VaultService()).GetSshPasswordForProfile(profile));
```

Apply the identical change at `src/NovaTerminal.App/Services/Ssh/RemotePathAutocompleteService.cs:33` and `src/NovaTerminal.App/Shell/SftpService.cs:596`. Add `using` for the `MainWindow` namespace if needed (it is in the root app namespace; confirm by checking an existing reference, e.g. how `MainWindow` is referenced elsewhere in `Services/Ssh`). If a direct type reference is awkward across namespaces, leave that specific site as `new VaultService()` — with keychain backing it still reads the same secrets — and note it.

> `SshInteractionService.cs:38` default param and `SshAskPassCommand.cs` are intentionally left as `new VaultService()` per the spec (askpass is a separate process; the interaction service is typically constructed with an explicit vault by its callers).

- [ ] **Step 4: Build and run the full app test project**

Run: `scripts/build.ps1 build src/NovaTerminal.App`
Expected: SUCCESS.

Run: `scripts/build.ps1 test tests/NovaTerminal.App.Tests`
Expected: PASS (no regressions in the migrated/new tests).

- [ ] **Step 5: Commit**

```bash
git add src/NovaTerminal.App/MainWindow.axaml.cs src/NovaTerminal.App/Services/Ssh/RemoteDirectoryBrowserService.cs src/NovaTerminal.App/Services/Ssh/RemotePathAutocompleteService.cs src/NovaTerminal.App/Shell/SftpService.cs
git commit -m "feat: surface vault-unavailable banner; share VaultService instance (#100)"
```

---

## Task 8: Final verification + manual native validation

- [ ] **Step 1: Full affected-project test run**

Run: `scripts/build.ps1 test tests/NovaTerminal.App.Tests`
Expected: PASS, no skips other than the keychain integration test on Windows.

- [ ] **Step 2: Confirm no weak-crypto remnants**

Run: `rtk grep "GetPlatformKey|NovaVaultSalt|EncryptFallback|machine-id" src`
Expected: no matches in `src/` (only the spec/plan docs may mention them).

- [ ] **Step 3: Windows manual smoke test**

Per the project's GUI smoke-test preference, do this manually:
1. Launch the app (`scripts/build.ps1 run src/NovaTerminal.App` or the usual run path).
2. Create an SSH profile, connect, enter a password with "remember" checked.
3. Restart the app; reconnect — confirm the password is remembered (Credential Manager path still works).

- [ ] **Step 4: macOS + Linux manual validation (REQUIRED before PR)**

On a real macOS machine and a Linux desktop session (or VM with GNOME Keyring unlocked):
1. Build & run; repeat the remember-password round-trip from Step 3.
2. Confirm the secret appears in the OS keychain (macOS: Keychain Access → "NovaTerminal"; Linux: `secret-tool search key "SSH:PROFILE:<guid>"` or Seahorse).
3. Confirm restart-and-reconnect remembers the password.
4. Headless check (Linux, no keyring): run with the keyring locked/absent; confirm the app launches, shows the "Credential storage unavailable" toast, and SSH still connects (just doesn't persist).
5. Legacy check: drop a dummy `vault.dat` in the app data dir, launch, confirm it's deleted.

- [ ] **Step 5: Push branch and open PR**

```bash
git push -u origin fix/issue-100-vault-keychain-security
gh pr create --title "Fix #100: vault secrets use OS keychain on Linux/macOS" --body "<summary + closes #100 + manual-validation checklist results>"
```

---

## Self-Review

**Spec coverage:**
- Goal 1 (OS-managed per-user secret) → Tasks 4, 5. ✓
- Goal 2 (no machine-derived key path) → Task 2 deletes it; Task 8 Step 2 verifies. ✓
- Goal 3 (surface failure, disable persistence) → Task 2 disabled-mode + Task 7 banner. ✓
- Goal 4 (delete legacy file) → Task 3. ✓
- Goal 5 (consistent construction) → Task 7 shared instance. ✓
- `ISecretStore` + InMemory → Task 1. ✓
- Test migration of 14 sites → Task 2 Steps 5–6. ✓
- Skippable integration tests → Task 6. ✓
- `SshAskPassCommand` left alone → noted in Task 7. ✓

**Placeholder scan:** No TBD/TODO. The `<summary>` in Task 8 Step 5 is a PR body the author fills at submit time, not a code placeholder.

**Type consistency:** `ISecretStore` members (`IsAvailable`, `Read`, `Write`, `Delete`) are used identically across Tasks 1–6. `VaultService(ISecretStore)`, `PersistenceAvailable`, `SecretStore.CreateDefault()`, `SecretStore.DeleteLegacyVaultFile(string)`, `AppPaths.LegacyVaultFilePath` are defined before use. ✓

**Known risk:** Tasks 4–5 P/Invoke cannot be validated on Windows/CI; Task 8 Step 4 is the required manual gate. The libsecret `BuildAttributes` per-call leak and the SecretSchema/CoreFoundation marshalling are the highest-risk spots — the dotnet-pinvoke sub-skill is mandated for both.
