namespace NovaTerminal.Core.Ssh.Launch;

public enum SshDiagnosticsLevel
{
    None = 0,
    Verbose = 1,
    VeryVerbose = 2
}

public static class SshDiagnosticsLevelExtensions
{
    public static IReadOnlyList<string> ToArguments(this SshDiagnosticsLevel level)
    {
        return level switch
        {
            SshDiagnosticsLevel.Verbose => new[] { "-v" },
            SshDiagnosticsLevel.VeryVerbose => new[] { "-vv" },
            _ => Array.Empty<string>()
        };
    }
}
