using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NovaTerminal.Shell.Secrets
{
    /// <summary>
    /// macOS secret store backed by Keychain Services (login keychain, per-user).
    /// Items are generic-password records keyed by
    /// <c>kSecAttrService = "NovaTerminal"</c> and <c>kSecAttrAccount = key</c>, with
    /// the secret stored as the UTF-8 bytes of the value.
    /// </summary>
    /// <remarks>
    /// The Security and CoreFoundation frameworks only load on macOS. On
    /// Windows/Linux they are absent; construction is written so that it never
    /// throws there — it merely reports <see cref="IsAvailable"/> as
    /// <see langword="false"/>.
    ///
    /// This is why <em>all</em> native resolution (framework handles and the
    /// CoreFoundation/Security constant symbols) happens inside the constructor's
    /// try/catch via <see cref="LoadNative"/>, never in <c>static</c> field
    /// initializers. Resolving <c>NativeLibrary.Load</c>/<c>GetExport</c> from a
    /// static initializer on a non-macOS box faults during <em>type</em>
    /// initialization, which surfaces as <see cref="TypeInitializationException"/>
    /// and would slip past the constructor's catch blocks. Keeping resolution
    /// instance-scoped mirrors the failure-safe pattern in
    /// <see cref="LinuxSecretStore"/>, and matters because a later task wires
    /// <c>SecretStore.CreateDefault()</c> to call <c>new MacKeychainStore()</c>
    /// unconditionally on macOS-or-otherwise startup paths.
    /// </remarks>
    public sealed class MacKeychainStore : ISecretStore
    {
        private const string Sec = "/System/Library/Frameworks/Security.framework/Security";
        private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string ServiceName = "NovaTerminal";

        private const int errSecSuccess = 0;
        private const int errSecItemNotFound = -25300;
        private const uint kCFStringEncodingUTF8 = 0x08000100;

        // CFTypeRef constants. These are exported as *symbols whose storage holds a
        // CFTypeRef pointer*, so the usable value is one dereference past the export
        // address: Marshal.ReadIntPtr(GetExport(...)). Getting this wrong (using the
        // symbol address directly) would put a pointer-to-a-pointer into the query
        // dictionary and silently mismatch every lookup.
        private IntPtr _kSecClass;
        private IntPtr _kSecClassGenericPassword;
        private IntPtr _kSecAttrService;
        private IntPtr _kSecAttrAccount;
        private IntPtr _kSecValueData;
        private IntPtr _kSecReturnData;
        private IntPtr _kSecMatchLimit;
        private IntPtr _kSecMatchLimitOne;
        private IntPtr _kCFBooleanTrue;

        // CFDictionary callback tables. Unlike the constants above these are exported
        // *structs*; the value the API wants is the address OF the struct, i.e. the
        // export address itself — NO dereference.
        private IntPtr _kCFTypeDictionaryKeyCallBacks;
        private IntPtr _kCFTypeDictionaryValueCallBacks;

        private readonly bool _available;

        public MacKeychainStore()
        {
            try
            {
                LoadNative();

                // Probe Keychain Services. A missing framework throws (caught below);
                // a present framework with nothing stored returns errSecItemNotFound,
                // which ReadInternal maps to null without throwing. Call ReadInternal
                // (not Read) because _available is not set yet.
                _ = ReadInternal("__novaterminal_probe__");
                _available = true;
            }
            catch (DllNotFoundException) { _available = false; }
            catch (EntryPointNotFoundException) { _available = false; }
            catch (BadImageFormatException) { _available = false; }
        }

        public bool IsAvailable => _available;

        public string? Read(string key)
        {
            if (!_available)
            {
                return null;
            }

            return ReadInternal(key);
        }

        // Performs the actual keychain lookup. Called by the constructor probe before
        // _available is set, so it must not be gated on _available.
        private string? ReadInternal(string key)
        {
            IntPtr query = BuildQuery(key, forReturnData: true);
            try
            {
                int status = SecItemCopyMatching(query, out IntPtr dataRef);
                if (status == errSecItemNotFound)
                {
                    return null;
                }

                if (status != errSecSuccess || dataRef == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    // dataRef came back via kSecReturnData under the Copy/Create
                    // ownership rule => we own it and must CFRelease it (the finally).
                    IntPtr bytes = CFDataGetBytePtr(dataRef);
                    nint len = CFDataGetLength(dataRef);
                    if (bytes == IntPtr.Zero || len <= 0)
                    {
                        return string.Empty;
                    }

                    var buffer = new byte[(int)len];
                    // bytes is a buffer borrowed from dataRef; copy it out, never free it.
                    Marshal.Copy(bytes, buffer, 0, (int)len);
                    return Encoding.UTF8.GetString(buffer);
                }
                finally
                {
                    CFRelease(dataRef);
                }
            }
            finally
            {
                CFRelease(query);
            }
        }

        public void Write(string key, string value)
        {
            if (!_available)
            {
                return;
            }

            byte[] valueBytes = Encoding.UTF8.GetBytes(value);
            IntPtr matchQuery = BuildQuery(key, forReturnData: false);
            IntPtr attrsToUpdate = CFDictionaryCreateMutable();
            IntPtr valueData = CFDataCreate(IntPtr.Zero, valueBytes, valueBytes.Length);
            try
            {
                CFDictionaryAddValue(attrsToUpdate, _kSecValueData, valueData);
                int status = SecItemUpdate(matchQuery, attrsToUpdate);
                if (status == errSecItemNotFound)
                {
                    IntPtr addQuery = BuildQuery(key, forReturnData: false);
                    try
                    {
                        // valueData is added (retained by the dictionary), NOT released
                        // here — it is released exactly once in the outer finally.
                        CFDictionaryAddValue(addQuery, _kSecValueData, valueData);
                        _ = SecItemAdd(addQuery, IntPtr.Zero);
                    }
                    finally
                    {
                        CFRelease(addQuery);
                    }
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
            if (!_available)
            {
                return false;
            }

            IntPtr query = BuildQuery(key, forReturnData: false);
            try
            {
                int status = SecItemDelete(query);
                return status == errSecSuccess;
            }
            finally
            {
                CFRelease(query);
            }
        }

        // Builds a CFDictionary query/attribute set. The CFStrings created for the
        // service and account are retained by CFDictionaryAddValue, so they are
        // released here immediately after insertion; the dictionary keeps its own
        // reference. The caller owns (and must CFRelease) the returned dictionary.
        private IntPtr BuildQuery(string key, bool forReturnData)
        {
            IntPtr dict = CFDictionaryCreateMutable();
            CFDictionaryAddValue(dict, _kSecClass, _kSecClassGenericPassword);

            IntPtr service = CFStr(ServiceName);
            IntPtr account = CFStr(key);
            CFDictionaryAddValue(dict, _kSecAttrService, service);
            CFDictionaryAddValue(dict, _kSecAttrAccount, account);
            CFRelease(service);
            CFRelease(account);

            if (forReturnData)
            {
                CFDictionaryAddValue(dict, _kSecReturnData, _kCFBooleanTrue);
                CFDictionaryAddValue(dict, _kSecMatchLimit, _kSecMatchLimitOne);
            }

            return dict;
        }

        private static IntPtr CFStr(string s)
            => CFStringCreateWithCString(IntPtr.Zero, s, kCFStringEncodingUTF8);

        private IntPtr CFDictionaryCreateMutable()
            => CFDictionaryCreateMutable(IntPtr.Zero, 0, _kCFTypeDictionaryKeyCallBacks, _kCFTypeDictionaryValueCallBacks);

        // Resolves both frameworks and all required constants. Any missing framework
        // or symbol throws a DllNotFoundException / EntryPointNotFoundException /
        // BadImageFormatException that the constructor catches, leaving the store
        // unavailable rather than letting the exception escape.
        private void LoadNative()
        {
            IntPtr sec = NativeLibrary.Load(Sec);
            IntPtr cf = NativeLibrary.Load(CF);

            _kSecClass = DerefConst(sec, "kSecClass");
            _kSecClassGenericPassword = DerefConst(sec, "kSecClassGenericPassword");
            _kSecAttrService = DerefConst(sec, "kSecAttrService");
            _kSecAttrAccount = DerefConst(sec, "kSecAttrAccount");
            _kSecValueData = DerefConst(sec, "kSecValueData");
            _kSecReturnData = DerefConst(sec, "kSecReturnData");
            _kSecMatchLimit = DerefConst(sec, "kSecMatchLimit");
            _kSecMatchLimitOne = DerefConst(sec, "kSecMatchLimitOne");

            _kCFBooleanTrue = DerefConst(cf, "kCFBooleanTrue");

            // Struct exports: take the symbol address directly (no dereference).
            _kCFTypeDictionaryKeyCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
            _kCFTypeDictionaryValueCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
        }

        // CFTypeRef constant: the export points at storage holding the pointer, so
        // dereference once to get the actual CFTypeRef value.
        private static IntPtr DerefConst(IntPtr handle, string name)
            => Marshal.ReadIntPtr(NativeLibrary.GetExport(handle, name));

        // ---- Security.framework ----

        [DllImport(Sec)]
        private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

        [DllImport(Sec)]
        private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

        [DllImport(Sec)]
        private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

        [DllImport(Sec)]
        private static extern int SecItemDelete(IntPtr query);

        // ---- CoreFoundation.framework ----

        [DllImport(CF)]
        private static extern void CFRelease(IntPtr cf);

        // CA2101: the parameter is explicitly UTF-8 marshalled per-parameter; the
        // BestFitMapping/ThrowOnUnmappableChar pair satisfies the analyzer.
        [DllImport(CF, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern IntPtr CFStringCreateWithCString(
            IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

        // CFIndex is a pointer-sized signed integer (nint) — 64-bit on 64-bit macOS.
        [DllImport(CF)]
        private static extern IntPtr CFDataCreate(IntPtr alloc, byte[] bytes, nint length);

        [DllImport(CF)]
        private static extern IntPtr CFDataGetBytePtr(IntPtr data);

        [DllImport(CF)]
        private static extern nint CFDataGetLength(IntPtr data);

        [DllImport(CF)]
        private static extern IntPtr CFDictionaryCreateMutable(
            IntPtr alloc, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);

        [DllImport(CF)]
        private static extern void CFDictionaryAddValue(IntPtr dict, IntPtr key, IntPtr value);
    }
}
