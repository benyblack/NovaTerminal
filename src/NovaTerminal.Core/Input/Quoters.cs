namespace NovaTerminal.Core
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
            string escaped = path.Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }
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
