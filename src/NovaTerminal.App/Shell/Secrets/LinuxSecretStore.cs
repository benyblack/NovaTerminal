using System;
using System.Runtime.InteropServices;

namespace NovaTerminal.Shell.Secrets
{
    /// <summary>
    /// Linux secret store backed by libsecret / Secret Service (GNOME Keyring, KWallet).
    /// Secrets are protected by the user's login keyring, not by any machine-derived key.
    /// </summary>
    /// <remarks>
    /// Uses libsecret's non-varargs ("vectored") password API
    /// (<c>secret_password_*v_sync</c>) so we never have to marshal a C varargs call.
    /// Attributes are passed in a glib <c>GHashTable</c>. Items are namespaced by the
    /// schema name <c>com.novaterminal.Vault</c> and distinguished by a <c>"key"</c>
    /// attribute.
    ///
    /// This library only loads on Linux. On Windows/macOS the native libraries are
    /// absent; construction is written so that it never throws there — it merely
    /// reports <see cref="IsAvailable"/> as <see langword="false"/>. That matters
    /// because a later task wires <c>SecretStore.CreateDefault()</c> to call
    /// <c>new LinuxSecretStore()</c> unconditionally on every non-Windows/non-macOS
    /// start, and we must not surface a <see cref="TypeInitializationException"/> or
    /// <see cref="DllNotFoundException"/> from that path.
    /// </remarks>
    public sealed class LinuxSecretStore : ISecretStore
    {
        private const string Lib = "libsecret-1.so.0";
        private const string Glib = "libglib-2.0.so.0";
        private const string SchemaName = "com.novaterminal.Vault";
        private const string KeyAttribute = "key";

        // SecretSchemaFlags.SECRET_SCHEMA_NONE
        private const int SecretSchemaNone = 0;
        // SecretSchemaAttributeType.SECRET_SCHEMA_ATTRIBUTE_STRING
        private const int SecretSchemaAttributeString = 0;

        private readonly IntPtr _schema;

        // glib comparison/hash function pointers, resolved lazily so that a machine
        // without glib does not fault during *static* initialization (which would
        // surface as TypeInitializationException, defeating the ctor's catch blocks).
        private readonly IntPtr _gStrHash;
        private readonly IntPtr _gStrEqual;

        private readonly bool _available;

        public LinuxSecretStore()
        {
            try
            {
                // Resolve glib helpers first. On Windows/macOS this throws
                // DllNotFoundException, which we catch below -> IsAvailable == false.
                IntPtr glib = NativeLibrary.Load(Glib);
                _gStrHash = NativeLibrary.GetExport(glib, "g_str_hash");
                _gStrEqual = NativeLibrary.GetExport(glib, "g_str_equal");

                _schema = BuildSchema();

                // Probe the Secret Service. A missing native lib throws (caught);
                // a present lib with no running keyring sets a GError, which we treat
                // as "unavailable" rather than throwing.
                _ = LookupRaw("__novaterminal_probe__", out bool serviceError);
                _available = !serviceError;
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

            return LookupRaw(key, out _);
        }

        public void Write(string key, string value)
        {
            if (!_available)
            {
                return;
            }

            IntPtr attrs = BuildAttributes(key);
            try
            {
                // libsecret copies the label and password synchronously; the
                // marshalled UTF-8 strings only need to live for the duration of
                // the call, so default LPUTF8Str marshalling is safe here.
                _ = secret_password_storev_sync(
                    _schema, attrs, IntPtr.Zero,
                    label: $"NovaTerminal: {key}", password: value,
                    cancellable: IntPtr.Zero, error: out IntPtr error);
                FreeError(error);
            }
            finally
            {
                DestroyAttributes(attrs);
            }
        }

        public bool Delete(string key)
        {
            if (!_available)
            {
                return false;
            }

            IntPtr attrs = BuildAttributes(key);
            try
            {
                int removed = secret_password_clearv_sync(_schema, attrs, IntPtr.Zero, out IntPtr error);
                FreeError(error);
                return removed != 0;
            }
            finally
            {
                DestroyAttributes(attrs);
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
                if (result == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUTF8(result);
                }
                finally
                {
                    // Password buffers are non-pageable memory allocated by
                    // libsecret; they must be returned with secret_password_free,
                    // never Marshal.Free*.
                    secret_password_free(result);
                }
            }
            finally
            {
                DestroyAttributes(attrs);
            }
        }

        // Builds a GHashTable<string,string> with a single { "key" -> <key> } entry.
        //
        // We use g_hash_table_new_full with g_free as both the key- and value-destroy
        // notify, and hand it heap copies of the UTF-8 strings (g_strdup). That makes
        // the table the sole owner of those allocations, so DestroyAttributes frees
        // everything with one call and there is no leak. (The simpler g_hash_table_new
        // form has no destroy-notify, which would leak the duplicated key/value on
        // every vault op.)
        private IntPtr BuildAttributes(string key)
        {
            IntPtr table = g_hash_table_new_full(_gStrHash, _gStrEqual, _gFree, _gFree);

            // g_strdup copies into glib-owned memory that g_free (the destroy notify)
            // can release. Marshal a temporary native UTF-8 buffer, hand it to
            // g_strdup, then release our temporary.
            IntPtr keyName = NativeUtf8Dup(KeyAttribute);
            IntPtr keyValue = NativeUtf8Dup(key);
            g_hash_table_insert(table, keyName, keyValue);
            return table;
        }

        private static void DestroyAttributes(IntPtr table)
        {
            if (table != IntPtr.Zero)
            {
                g_hash_table_destroy(table);
            }
        }

        // Duplicates a managed string into glib-heap UTF-8 memory (freeable by g_free).
        private static IntPtr NativeUtf8Dup(string s)
        {
            IntPtr tmp = Marshal.StringToCoTaskMemUTF8(s);
            try
            {
                return g_strdup(tmp);
            }
            finally
            {
                Marshal.FreeCoTaskMem(tmp);
            }
        }

        // Lays out a SecretSchema on the native heap for 64-bit Linux:
        //
        //   struct SecretSchema {
        //       const gchar *name;            // offset 0,  8 bytes
        //       SecretSchemaFlags flags;      // offset 8,  4 bytes (enum == int)
        //       /* 4 bytes padding to 8-byte alignment of the array element */
        //       struct {
        //           const gchar *name;        // 8 bytes
        //           SecretSchemaAttributeType type; // 4 bytes, +4 tail padding
        //       } attributes[32];             // 16-byte stride per element
        //       /* glib-private reserved fields follow; zeroing the whole block and
        //          terminating attributes with a NULL name is sufficient. */
        //   };
        //
        // The header (name + flags) is 12 bytes but the attribute array needs 8-byte
        // alignment because each element starts with a pointer, so the array begins at
        // offset 16. Each attribute element is { pointer; int } = 12 bytes of data
        // padded to a 16-byte stride. A NULL `name` on the first unused entry
        // terminates the list, which is why the trailing entries are left zeroed.
        private static IntPtr BuildSchema()
        {
            int pointerSize = IntPtr.Size;
            int intSize = sizeof(int);

            // Header: pointer (name) + int (flags), padded up to pointer alignment so
            // the following array element (which begins with a pointer) is aligned.
            int headerRaw = pointerSize + intSize;
            int headerPadded = Align(headerRaw, pointerSize);

            // Each attribute element: pointer (name) + int (type), padded so the next
            // element's leading pointer stays aligned.
            int attrStride = Align(pointerSize + intSize, pointerSize);
            const int attrCount = 32;

            int size = headerPadded + (attrStride * attrCount);
            IntPtr schema = Marshal.AllocHGlobal(size);

            // Zero the whole block: NONE flags, STRING attribute type (== 0), and the
            // NULL-name terminator for every unused attribute slot.
            for (int i = 0; i < size; i++)
            {
                Marshal.WriteByte(schema, i, 0);
            }

            // name (heap ANSI string; ASCII-only so ANSI == UTF-8 here). This lives for
            // the lifetime of the schema, which lives for the process lifetime of the
            // store, so it is intentionally never freed (single, bounded allocation).
            Marshal.WriteIntPtr(schema, 0, Marshal.StringToHGlobalAnsi(SchemaName));
            Marshal.WriteInt32(schema, pointerSize, SecretSchemaNone);

            // attributes[0] = { "key", SECRET_SCHEMA_ATTRIBUTE_STRING }
            int attr0 = headerPadded;
            Marshal.WriteIntPtr(schema, attr0, Marshal.StringToHGlobalAnsi(KeyAttribute));
            Marshal.WriteInt32(schema, attr0 + pointerSize, SecretSchemaAttributeString);

            return schema;
        }

        private static int Align(int value, int alignment)
            => (value + alignment - 1) / alignment * alignment;

        private static void FreeError(IntPtr error)
        {
            if (error != IntPtr.Zero)
            {
                g_error_free(error);
            }
        }

        // ---- libsecret (gboolean is a 4-byte gint; marshal as I4 and compare != 0) ----

        [DllImport(Lib)]
        private static extern IntPtr secret_password_lookupv_sync(
            IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        // BestFitMapping=false / ThrowOnUnmappableChar=true satisfy CA2101 (the
        // string params are already explicitly UTF-8 marshalled per-parameter).
        [DllImport(Lib, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        [return: MarshalAs(UnmanagedType.I4)]
        private static extern int secret_password_storev_sync(
            IntPtr schema, IntPtr attributes, IntPtr collection,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
            IntPtr cancellable, out IntPtr error);

        [DllImport(Lib)]
        [return: MarshalAs(UnmanagedType.I4)]
        private static extern int secret_password_clearv_sync(
            IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

        [DllImport(Lib)]
        private static extern void secret_password_free(IntPtr password);

        // ---- glib ----

        [DllImport(Glib)]
        private static extern IntPtr g_hash_table_new_full(
            IntPtr hashFunc, IntPtr keyEqualFunc, IntPtr keyDestroyFunc, IntPtr valueDestroyFunc);

        [DllImport(Glib)]
        private static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

        [DllImport(Glib)]
        private static extern void g_hash_table_destroy(IntPtr table);

        [DllImport(Glib)]
        private static extern IntPtr g_strdup(IntPtr str);

        [DllImport(Glib)]
        private static extern void g_error_free(IntPtr error);

        // g_free function pointer, resolved on first use for the GHashTable destroy
        // notifies. Resolved lazily (not in a static initializer) so a glib-less box
        // never faults at type load — see the class remarks.
        private IntPtr _gFreeCache;
        private IntPtr _gFree => _gFreeCache != IntPtr.Zero
            ? _gFreeCache
            : (_gFreeCache = NativeLibrary.GetExport(NativeLibrary.Load(Glib), "g_free"));
    }
}
