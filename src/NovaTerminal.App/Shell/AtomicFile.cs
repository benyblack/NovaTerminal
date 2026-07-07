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
            => Write(path, tmp => File.WriteAllText(tmp, contents));

        public static void WriteAllBytes(string path, byte[] bytes)
            => Write(path, tmp => File.WriteAllBytes(tmp, bytes));

        private static void Write(string path, Action<string> writeTemp)
        {
            // Unique temp sibling: concurrent savers of the same destination (e.g. two
            // VaultService instances over the same vault.dat) must not collide on a
            // shared ".tmp" name — last atomic move wins, nobody sees a torn file.
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                writeTemp(tmp);

                if (File.Exists(path))
                {
                    // Best-effort backup of the previous good content; the atomic move
                    // below must not be blocked by a backup failure of any kind.
                    try { File.Copy(path, path + ".bak", overwrite: true); }
                    catch { }
                }

                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // Don't leave orphaned temp files behind on failure.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
