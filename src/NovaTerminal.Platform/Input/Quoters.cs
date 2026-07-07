namespace NovaTerminal.Platform
{
    public class PwshQuoter : IShellQuoter
    {
        public string QuotePath(string path)
        {
            string escaped = path.Replace("'", "''");
            return $"'{escaped}'";
        }
    }

    public class CmdQuoter : IShellQuoter
    {
        public string QuotePath(string path)
        {
            // Windows file names cannot contain '"', so quoting is enough for whitespace.
            // The genuinely dangerous characters (see HasUnsafeMetacharacters) cannot be
            // neutralized in an *interactive* cmd session at all, so callers must refuse
            // those instead of relying on quoting.
            return $"\"{path}\"";
        }

        // In interactive cmd.exe, %VAR% expands even inside double quotes and there is no
        // escape for it (the batch-only "%%" doesn't apply), so a dropped file literally
        // named "%APPDATA%.txt" would inject the expanded environment value. !VAR! does
        // the same when delayed expansion is on. Neither can be quoted away, so a path
        // containing them is unsafe to auto-insert (#170).
        public bool HasUnsafeMetacharacters(string path)
            => path.Contains('%') || path.Contains('!');
    }

    public class PosixShQuoter : IShellQuoter
    {
        public string QuotePath(string path)
        {
            string escaped = path.Replace("'", "'\\''");
            return $"'{escaped}'";
        }
    }
}
