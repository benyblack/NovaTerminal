using System;
using System.IO;

namespace NovaTerminal;

internal static class VtReportCli
{
    public static bool TryRun(string[] args, TextWriter stdout, TextWriter stderr, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (!VtReportCommand.IsSupportedCliMode(args))
        {
            exitCode = 0;
            return false;
        }

        exitCode = VtReportCommand.Execute(args, stdout, stderr);
        return true;
    }
}
