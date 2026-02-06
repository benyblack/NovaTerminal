using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NovaTerminal.Core.Native
{
    public static class Win32CredentialManager
    {
        private const string DllName = "advapi32.dll";

        [DllImport(DllName, EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL userCredential, uint target, uint flags);

        [DllImport(DllName, EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, CREDENTIAL_TYPE type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport(DllName, EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, CREDENTIAL_TYPE type, int flags);

        [DllImport(DllName, SetLastError = true)]
        private static extern bool CredFree(IntPtr buffer);

        private enum CREDENTIAL_TYPE : uint
        {
            GENERIC = 1,
            DOMAIN_PASSWORD = 2,
            DOMAIN_VISIBLE_PASSWORD = 3
        }

        private enum CREDENTIAL_PERSIST : uint
        {
            SESSION = 1,
            LOCAL_MACHINE = 2,
            ENTERPRISE = 3
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        public static bool Write(string target, string username, string password)
        {
            var credential = new CREDENTIAL
            {
                Type = (uint)CREDENTIAL_TYPE.GENERIC,
                TargetName = Marshal.StringToCoTaskMemUni(target),
                UserName = Marshal.StringToCoTaskMemUni(username),
                Persist = (uint)CREDENTIAL_PERSIST.LOCAL_MACHINE,
                AttributeCount = 0,
                Attributes = IntPtr.Zero
            };

            byte[] blob = Encoding.Unicode.GetBytes(password);
            credential.CredentialBlobSize = (uint)blob.Length;
            credential.CredentialBlob = Marshal.AllocCoTaskMem(blob.Length);
            Marshal.Copy(blob, 0, credential.CredentialBlob, blob.Length);

            try
            {
                return CredWrite(ref credential, 0, 0);
            }
            finally
            {
                Marshal.FreeCoTaskMem(credential.TargetName);
                Marshal.FreeCoTaskMem(credential.UserName);
                Marshal.FreeCoTaskMem(credential.CredentialBlob);
            }
        }

        public static (string Username, string Password)? Read(string target)
        {
            IntPtr pCred;
            if (!CredRead(target, CREDENTIAL_TYPE.GENERIC, 0, out pCred))
            {
                return null;
            }

            try
            {
                var credential = Marshal.PtrToStructure<CREDENTIAL>(pCred);
                string username = Marshal.PtrToStringUni(credential.UserName) ?? "";

                string password = "";
                if (credential.CredentialBlobSize > 0 && credential.CredentialBlob != IntPtr.Zero)
                {
                    byte[] blob = new byte[credential.CredentialBlobSize];
                    Marshal.Copy(credential.CredentialBlob, blob, 0, (int)credential.CredentialBlobSize);
                    password = Encoding.Unicode.GetString(blob);
                }

                return (username, password);
            }
            finally
            {
                CredFree(pCred);
            }
        }

        public static bool Delete(string target)
        {
            return CredDelete(target, CREDENTIAL_TYPE.GENERIC, 0);
        }
    }
}
