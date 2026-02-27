using NovaTerminal.Core.Ssh.Models;
using NovaTerminal.Core.Ssh.OpenSsh;
using NovaTerminal.Core.Ssh.Storage;

namespace NovaTerminal.Core.Ssh.Launch;

public sealed class SshLaunchPlanner : ISshLaunchPlanner
{
    private readonly ISshProfileStore _profileStore;
    private readonly IOpenSshConfigCompiler _configCompiler;
    private readonly Func<string> _sshPathResolver;

    public SshLaunchPlanner(
        ISshProfileStore profileStore,
        IOpenSshConfigCompiler configCompiler,
        Func<string>? sshPathResolver = null)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _configCompiler = configCompiler ?? throw new ArgumentNullException(nameof(configCompiler));
        _sshPathResolver = sshPathResolver ?? ResolveSshExecutablePath;
    }

    public SshLaunchPlan Plan(Guid profileId, IReadOnlyList<string>? extraArgs = null)
    {
        SshProfile profile = _profileStore.GetProfile(profileId)
            ?? throw new InvalidOperationException($"SSH profile '{profileId}' was not found.");
        IReadOnlyList<SshProfile> profiles = _profileStore.GetProfiles();

        OpenSshCompilationResult result = _configCompiler.Compile(profiles, profileId);
        string executable = _sshPathResolver();

        var args = new List<string>
        {
            "-F",
            result.ConfigFilePath,
            result.Alias
        };

        foreach (string arg in ParseExtraArguments(profile.ExtraSshArgs))
        {
            args.Add(arg);
        }

        if (extraArgs != null)
        {
            foreach (string arg in extraArgs.Where(arg => !string.IsNullOrWhiteSpace(arg)))
            {
                args.Add(arg);
            }
        }

        return new SshLaunchPlan
        {
            ProfileId = profile.Id,
            SshExecutablePath = executable,
            ConfigFilePath = result.ConfigFilePath,
            Alias = result.Alias,
            Arguments = args.ToArray(),
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory) ? null : profile.WorkingDirectory
        };
    }

    public static string ResolveSshExecutablePath()
    {
        if (TryFindInPath(out string? pathFromPath))
        {
            return pathFromPath ?? throw new InvalidOperationException("PATH lookup returned no executable path.");
        }

        if (OperatingSystem.IsWindows())
        {
            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string fallbackPath = string.IsNullOrWhiteSpace(windowsDir)
                ? @"C:\Windows\System32\OpenSSH\ssh.exe"
                : Path.Combine(windowsDir, "System32", "OpenSSH", "ssh.exe");

            if (File.Exists(fallbackPath))
            {
                return fallbackPath;
            }
        }

        throw new FileNotFoundException("Unable to locate system OpenSSH executable (ssh).");
    }

    private static bool TryFindInPath(out string? resolvedPath)
    {
        string? envPath = Environment.GetEnvironmentVariable("PATH");
        resolvedPath = null;

        if (string.IsNullOrWhiteSpace(envPath))
        {
            return false;
        }

        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "ssh.exe", "ssh" }
            : new[] { "ssh" };

        foreach (string dir in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            foreach (string candidate in candidates)
            {
                string fullPath = Path.Combine(trimmed, candidate);
                if (File.Exists(fullPath))
                {
                    resolvedPath = fullPath;
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ParseExtraArguments(string? rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder(rawArguments.Length);
        bool inQuotes = false;
        char quoteChar = '\0';

        foreach (char ch in rawArguments)
        {
            if (inQuotes)
            {
                if (ch == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                inQuotes = true;
                quoteChar = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
