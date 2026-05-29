using NovaTerminal.Shell;
using System;
using System.Linq;
using NovaTerminal.CommandAssist.ShellIntegration.Contracts;
using NovaTerminal.Core;
using NovaTerminal.VT;

namespace NovaTerminal.CommandAssist.ShellIntegration.PowerShell;

public sealed class PowerShellShellIntegrationProvider : IShellIntegrationProvider
{
    public bool CanIntegrate(string? shellKind, TerminalProfile? profile)
    {
        if (string.Equals(shellKind, "pwsh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string command = profile?.Command ?? string.Empty;
        return command.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
               command.Contains("powershell", StringComparison.OrdinalIgnoreCase);
    }

    public ShellIntegrationLaunchPlan CreateLaunchPlan(string shellCommand, string? shellArguments, string? workingDirectory)
    {
        if (ContainsUserScriptFile(shellArguments))
        {
            return new ShellIntegrationLaunchPlan(
                IsIntegrated: false,
                ShellCommand: shellCommand,
                ShellArguments: shellArguments,
                BootstrapScriptPath: null);
        }

        string bootstrapScriptPath = PowerShellBootstrapBuilder.WriteScript(AppPaths.CommandAssistDirectory);
        string mergedArguments = BuildPowerShellArguments(shellArguments, bootstrapScriptPath);
        return new ShellIntegrationLaunchPlan(
            IsIntegrated: true,
            ShellCommand: shellCommand,
            ShellArguments: mergedArguments,
            BootstrapScriptPath: bootstrapScriptPath);
    }

    private static string BuildPowerShellArguments(string? shellArguments, string bootstrapScriptPath)
    {
        string original = shellArguments?.Trim() ?? string.Empty;

        // Pass the bootstrap path to -File UNQUOTED. PowerShell's -File parser
        // reads the raw command-line tail (not standard argv splitting), so any
        // surrounding double quotes would be retained as part of the path value
        // and rejected as "Illegal characters in path" (since '"' is an illegal
        // Windows path char). The generated bootstrap path lives under
        // %LOCALAPPDATA%\NovaTerminal\command-assist\ and only contains a space
        // if the Windows username does -- in that uncommon case we fall back to
        // the 8.3 short name, which has no spaces.
        string fileArgPath = ResolveSpacelessPath(bootstrapScriptPath);

        if (!original.Contains("-NoLogo", StringComparison.OrdinalIgnoreCase))
        {
            original = string.IsNullOrWhiteSpace(original)
                ? "-NoLogo"
                : $"-NoLogo {original}";
        }

        if (!original.Contains("-NoExit", StringComparison.OrdinalIgnoreCase))
        {
            original = string.IsNullOrWhiteSpace(original)
                ? "-NoExit"
                : $"{original} -NoExit";
        }

        if (!original.Contains("-File", StringComparison.OrdinalIgnoreCase))
        {
            original = string.IsNullOrWhiteSpace(original)
                ? $"-File {fileArgPath}"
                : $"{original} -File {fileArgPath}";
        }

        return original.Trim();
    }

    private static string ResolveSpacelessPath(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Any(char.IsWhiteSpace))
        {
            return path;
        }

        var buffer = new System.Text.StringBuilder(260);
        uint result = NativeMethods.GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        if (result == 0)
        {
            return path;
        }

        if (result > buffer.Capacity)
        {
            buffer.EnsureCapacity((int)result);
            result = NativeMethods.GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
            if (result == 0)
            {
                return path;
            }
        }

        string shortPath = buffer.ToString();
        return shortPath.Any(char.IsWhiteSpace) ? path : shortPath;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern uint GetShortPathNameW(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string lpszLongPath,
            System.Text.StringBuilder lpszShortPath,
            uint cchBuffer);
    }

    private static bool ContainsUserScriptFile(string? shellArguments)
    {
        return !string.IsNullOrWhiteSpace(shellArguments) &&
               shellArguments.Contains("-File", StringComparison.OrdinalIgnoreCase);
    }
}
