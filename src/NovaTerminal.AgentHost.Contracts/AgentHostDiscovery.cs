using System;
using System.IO;

namespace NovaTerminal.AgentHost.Contracts;

/// <summary>
/// Resolves where the endpoint discovery file lives. Lives in the contracts
/// leaf so both ends of the channel — the app (writer) and the MCP server
/// (reader), which share no other assemblies — agree on one path by
/// construction. Mirrors the app's AppPaths.RootDirectory resolution,
/// including the NOVATERM_APPDATA_ROOT override used by tests and portable
/// installs.
/// </summary>
public static class AgentHostDiscovery
{
    private const string AppName = "NovaTerminal";
    public const string RootOverrideEnvVar = "NOVATERM_APPDATA_ROOT";

    /// <summary>The app-data directory the discovery file is written into.</summary>
    public static string GetDefaultDirectory()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(RootOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);
    }

    /// <summary>Full path of the discovery file (<see cref="AgentHostProtocol.DiscoveryFileName"/>).</summary>
    public static string GetDefaultDiscoveryFilePath()
        => Path.Combine(GetDefaultDirectory(), AgentHostProtocol.DiscoveryFileName);
}
