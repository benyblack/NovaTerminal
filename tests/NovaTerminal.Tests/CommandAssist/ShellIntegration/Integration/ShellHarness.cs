using System.Diagnostics;
using System.Text;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration.Integration;

internal sealed record HarnessResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    IReadOnlyList<OscEvent> Events);

internal sealed record OscEvent(string Kind, string? Payload)
{
    public string? DecodedCommand
    {
        get
        {
            if (Kind != "C" || Payload is null) return null;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(Payload)); }
            catch { return null; }
        }
    }

    public (int? exitCode, long? durationMs) DecodedFinish
    {
        get
        {
            if (Kind != "D" || Payload is null) return (null, null);
            string[] parts = Payload.Split(';');
            int? exit = parts.Length > 0 && int.TryParse(parts[0], out int e) ? e : null;
            long? dur = parts.Length > 1 && long.TryParse(parts[1], out long d) ? d : null;
            return (exit, dur);
        }
    }
}

/// <summary>
/// Spawns a real shell with our generated bootstrap and captures the OSC
/// lifecycle the shell emits. Intended for integration coverage that
/// the substring-based bootstrap-builder tests cannot reach.
/// </summary>
internal static class ShellHarness
{
    public static string? FindBash()
    {
        // Prefer Git Bash on Windows so we get a real GNU bash, not WSL's
        // C:\Windows\system32\bash.exe which forwards into a separate Linux
        // distro and is too heavyweight + slow for unit tests.
        if (OperatingSystem.IsWindows())
        {
            foreach (string path in new[]
            {
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files\Git\usr\bin\bash.exe",
                @"C:\Program Files (x86)\Git\bin\bash.exe",
            })
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        return FindOnPath("bash");
    }

    public static string? FindZsh() => OperatingSystem.IsWindows() ? null : FindOnPath("zsh");

    public static string? FindFish() => OperatingSystem.IsWindows() ? null : FindOnPath("fish");

    private static string? FindOnPath(string name)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null) return null;
        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static HarnessResult Run(
        string shellPath,
        string arguments,
        string scriptedStdin,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = shellPath,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (environmentOverrides is not null)
        {
            foreach (var kv in environmentOverrides)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        // Force a predictable locale so OSC output isn't disturbed by
        // user-locale variations on test runners.
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {shellPath}");
        proc.StandardInput.Write(scriptedStdin);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Shell {shellPath} did not exit within timeout");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        var events = ParseOsc(stdout);
        return new HarnessResult(stdout, stderr, proc.ExitCode, events);
    }

    /// <summary>
    /// Minimal OSC parser for test consumption. Recognizes ESC ] ... BEL
    /// and ESC ] ... ESC \ terminators. Only emits events for OSC 7 and
    /// OSC 133;{A,B,C,D}; ignores other OSC traffic.
    /// </summary>
    public static IReadOnlyList<OscEvent> ParseOsc(string stdout)
    {
        var events = new List<OscEvent>();
        int i = 0;
        while (i < stdout.Length)
        {
            if (stdout[i] != '\x1b' || i + 1 >= stdout.Length || stdout[i + 1] != ']')
            {
                i++;
                continue;
            }

            int start = i + 2;
            int end = start;
            while (end < stdout.Length)
            {
                char c = stdout[end];
                if (c == '\x07') break;
                if (c == '\x1b' && end + 1 < stdout.Length && stdout[end + 1] == '\\') break;
                end++;
            }

            if (end >= stdout.Length) break;

            string body = stdout[start..end];
            i = end + (stdout[end] == '\x1b' ? 2 : 1);

            // OSC 7: cwd notification, e.g. "7;file://host/path"
            if (body.StartsWith("7;"))
            {
                events.Add(new OscEvent("7", body[2..]));
                continue;
            }

            // OSC 133 lifecycle: "133;A", "133;B", "133;C[;payload]", "133;D[;exit[;dur]]"
            if (body.StartsWith("133;"))
            {
                string rest = body[4..];
                if (rest.Length == 0) continue;
                char kind = rest[0];
                if (kind is 'A' or 'B' or 'C' or 'D')
                {
                    string? payload = rest.Length > 2 && rest[1] == ';' ? rest[2..] : null;
                    events.Add(new OscEvent(kind.ToString(), payload));
                }
            }
        }
        return events;
    }
}
