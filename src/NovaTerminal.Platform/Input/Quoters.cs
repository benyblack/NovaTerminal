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
            // Wrap in quotes for whitespace. Callers MUST first reject paths flagged by
            // HasUnsafeMetacharacters — those can't be neutralized in an interactive cmd
            // session, so quoting alone is not a security boundary here.
            return $"\"{path}\"";
        }

        // Characters that can't be neutralized in an *interactive* cmd.exe session, so a
        // path containing them is unsafe to auto-insert (#170):
        //   %  — %VAR% expands even inside double quotes; no escape exists (batch-only "%%"
        //        doesn't apply interactively), so "%APPDATA%.txt" injects the env value.
        //   !  — !VAR! delayed expansion, same problem when enabled.
        //   "  — Windows file names can't contain it, but a WSL-mapped Linux path passed to
        //        a cmd session can; an unescaped quote closes our wrapping quote and lets
        //        the rest of the name run as commands (e.g. foo" & del x "bar).
        public bool HasUnsafeMetacharacters(string path)
            => path.Contains('%') || path.Contains('!') || path.Contains('"');
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
