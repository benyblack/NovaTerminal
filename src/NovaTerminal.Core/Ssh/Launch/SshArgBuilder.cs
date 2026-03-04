using System.Globalization;
using System.Text.RegularExpressions;
using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.Launch;

public static class SshArgBuilder
{
    public static IReadOnlyList<string> BuildArguments(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string host = profile.Host?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("SSH host is required.", nameof(profile));
        }

        var args = new List<string>();
        int port = profile.Port > 0 ? profile.Port : 22;

        if (profile.AuthMode == SshAuthMode.IdentityFile && !string.IsNullOrWhiteSpace(profile.IdentityFilePath))
        {
            args.Add("-i");
            args.Add(profile.IdentityFilePath.Trim());
        }

        if (port != 22)
        {
            args.Add("-p");
            args.Add(port.ToString(CultureInfo.InvariantCulture));
        }

        string jumpChain = BuildJumpChain(profile.JumpHops);
        if (!string.IsNullOrEmpty(jumpChain))
        {
            args.Add("-J");
            args.Add(jumpChain);
        }

        foreach (var forward in profile.Forwards)
        {
            AddForwardArguments(args, forward);
        }

        AddMuxArguments(args, profile.MuxOptions);

        args.Add(FormatTarget(profile.User, host));
        return args;
    }

    public static string BuildCommandLine(SshProfile profile)
    {
        IReadOnlyList<string> args = BuildArguments(profile);
        return BuildCommandLine(args);
    }

    public static string BuildCommandLine(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(QuoteToken));
    }

    public static string SanitizeForLog(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return commandLine;
        }

        bool showFullArgs = Environment.GetEnvironmentVariable("NOVA_SSH_FULL_ARGS") == "1";
        if (showFullArgs)
        {
            // Just scrub identity paths if enabled, but full args requested
            return Regex.Replace(
                commandLine,
                "(^|\\s)-i\\s+(\"[^\"]+\"|\\S+)",
                "$1-i <identity-file>",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        // Standard sanitization: Only show config file and alias. Redact ExtraSshArgs and other flags.
        // The command line has a known structure built by SshLaunchPlanner: "-F config_path alias [extra_args...]"
        var match = Regex.Match(commandLine, @"-F\s+("".*?""|\S+)\s+(nova_[a-fA-F0-9]+)", RegexOptions.CultureInvariant);
        if (match.Success)
        {
            return $"-F {match.Groups[1].Value} {match.Groups[2].Value} <args-redacted>";
        }

        return "<command-line-redacted>";
    }

    private static void AddMuxArguments(List<string> args, SshMuxOptions? muxOptions)
    {
        if (muxOptions is null || !muxOptions.Enabled)
        {
            return;
        }

        if (muxOptions.ControlMasterAuto)
        {
            args.Add("-o");
            args.Add("ControlMaster=auto");
        }

        if (muxOptions.ControlPersistSeconds > 0)
        {
            args.Add("-o");
            args.Add($"ControlPersist={muxOptions.ControlPersistSeconds.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(muxOptions.ControlPath))
        {
            args.Add("-o");
            args.Add($"ControlPath={muxOptions.ControlPath.Trim()}");
        }
    }

    private static void AddForwardArguments(List<string> args, PortForward forward)
    {
        ArgumentNullException.ThrowIfNull(forward);

        switch (forward.Kind)
        {
            case PortForwardKind.Local:
                args.Add("-L");
                args.Add($"{FormatBindAndPort(forward.BindAddress, forward.SourcePort)}:{FormatDestination(forward)}");
                break;
            case PortForwardKind.Remote:
                args.Add("-R");
                args.Add($"{FormatBindAndPort(forward.BindAddress, forward.SourcePort)}:{FormatDestination(forward)}");
                break;
            case PortForwardKind.Dynamic:
                args.Add("-D");
                args.Add(FormatBindAndPort(forward.BindAddress, forward.SourcePort));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(forward.Kind), forward.Kind, "Unknown port forward kind.");
        }
    }

    private static string BuildJumpChain(IEnumerable<SshJumpHop> hops)
    {
        var entries = new List<string>();

        foreach (SshJumpHop hop in hops)
        {
            if (string.IsNullOrWhiteSpace(hop.Host))
            {
                continue;
            }

            int port = hop.Port > 0 ? hop.Port : 22;
            string target = FormatTarget(hop.User, hop.Host.Trim());
            if (port != 22)
            {
                target = $"{target}:{port.ToString(CultureInfo.InvariantCulture)}";
            }

            entries.Add(target);
        }

        return string.Join(',', entries);
    }

    private static string FormatTarget(string? user, string host)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return host;
        }

        return $"{user.Trim()}@{host}";
    }

    private static string FormatBindAndPort(string? bindAddress, int port)
    {
        if (port <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Forward port must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(bindAddress))
        {
            return port.ToString(CultureInfo.InvariantCulture);
        }

        return $"{bindAddress.Trim()}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatDestination(PortForward forward)
    {
        if (forward.DestinationPort <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(forward.DestinationPort), "Destination port must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(forward.DestinationHost))
        {
            throw new ArgumentException("Destination host is required for local/remote forwarding.", nameof(forward));
        }

        return $"{forward.DestinationHost.Trim()}:{forward.DestinationPort.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string QuoteToken(string token)
    {
        if (token.Length == 0)
        {
            return "\"\"";
        }

        bool requiresQuotes = token.Any(char.IsWhiteSpace) || token.Contains('"');
        if (!requiresQuotes)
        {
            return token;
        }

        return $"\"{token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
