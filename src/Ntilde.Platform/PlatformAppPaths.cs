namespace NovaTerminal.Platform;

/// <summary>
/// Where <c>NovaTerminal.Platform</c> keeps its on-disk state, honouring the
/// <c>NOVATERM_APPDATA_ROOT</c> override.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppPaths</c> in <c>NovaTerminal.App</c> is the authority on this for the application, but
/// <c>Platform</c> does not reference <c>App</c> (the dependency runs the other way), so code down
/// here cannot call it. Two places resolved the root themselves instead and neither consulted the
/// override: <c>JsonSshProfileStore</c> and <c>OpenSshConfigCompiler</c>. Both therefore read and
/// wrote the machine's real <c>%LOCALAPPDATA%\NovaTerminal\ssh</c> even when the whole process had
/// been redirected somewhere else — so a test, a portable install, or a capture harness got a
/// correctly sandboxed <c>settings.json</c>, logs and sessions, and then reached straight past the
/// sandbox for SSH data (#406).
/// </para>
/// <para>
/// This mirrors what <c>AgentHostDiscovery</c> already does in <c>NovaTerminal.AgentHost.Contracts</c>
/// for the same reason and in the same layer-independent way: read the variable directly rather than
/// depend upwards. The variable name is repeated across the three assemblies deliberately — sharing
/// it would mean a project reference in the direction the architecture forbids — so if it ever
/// changes, all three move together.
/// </para>
/// </remarks>
public static class PlatformAppPaths
{
    private const string AppName = "NovaTerminal";
    private const string RootOverrideEnvVar = "NOVATERM_APPDATA_ROOT";

    /// <summary>
    /// The application data root: <c>NOVATERM_APPDATA_ROOT</c> when set, otherwise
    /// <c>%LOCALAPPDATA%\NovaTerminal</c> (and the platform equivalent elsewhere).
    /// </summary>
    /// <remarks>
    /// The override is used verbatim as the root, with no <c>NovaTerminal</c> segment appended —
    /// matching <c>AppPaths.RootDirectory</c> and <c>AgentHostDiscovery.GetDefaultDirectory</c>, so
    /// a single override value points every assembly at the same tree.
    /// </remarks>
    public static string RootDirectory
    {
        get
        {
            string? overrideRoot = Environment.GetEnvironmentVariable(RootOverrideEnvVar);
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return Path.GetFullPath(overrideRoot);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName);
        }
    }

    /// <summary>The directory holding SSH profiles, generated OpenSSH config, and known-hosts.</summary>
    public static string SshDirectory => Path.Combine(RootDirectory, "ssh");
}
