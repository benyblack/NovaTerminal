using System;
using System.IO;

namespace NovaTerminal.Shell
{
    /// <summary>
    /// Crash-safe file persistence: write to a temp sibling, then atomically move it
    /// over the destination, keeping the previous content as <c>&lt;path&gt;.bak</c>.
    /// A crash mid-write can no longer corrupt the destination (#167). Same pattern
    /// as <c>JsonSshProfileStore</c> / <c>OpenSshConfigCompiler</c> in Platform.
    /// </summary>
    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string contents)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            ReplaceWithBackup(tmp, path);
        }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            ReplaceWithBackup(tmp, path);
        }

        private static void ReplaceWithBackup(string tmp, string path)
        {
            if (File.Exists(path))
            {
                // Best-effort backup of the previous good content; the atomic move
                // below must not be blocked by a backup failure.
                try { File.Copy(path, path + ".bak", overwrite: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            File.Move(tmp, path, overwrite: true);
        }
    }
}
