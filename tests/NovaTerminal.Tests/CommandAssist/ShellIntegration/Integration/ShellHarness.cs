using System.Text;
using NovaTerminal.Core;

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
/// Spawns a real shell with our generated bootstrap through the production
/// PTY path (<see cref="RustPtySession"/> + <see cref="AnsiParser"/>) and
/// captures the OSC lifecycle the shell emits via parser callbacks. This
/// exercises ConPTY/portable-pty end-to-end so the shell sees a real TTY
/// and bash -i / zsh -i / fish -i behave identically to a user terminal --
/// unlike the previous Process.Start + piped-stdin scheme, which hung
/// headless CI runners by leaving job-control-confused bash children alive.
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
        // On Windows, the rusty_pty default path uses ConPTY with the
        // PSEUDOCONSOLE_PASSTHROUGH flag set. That mode silently drops the
        // child's stdout when the calling process has no real attached
        // console -- exactly the case for xunit test hosts -- so we opt the
        // native side over to the portable-pty fallback (a plain ConPTY
        // without the passthrough flag) for the duration of this call.
        // Production (Avalonia app with a real GUI process) does not set
        // this and continues to use the original passthrough path.
        Environment.SetEnvironmentVariable("NOVA_PTY_NO_PASSTHROUGH", "1");

        // Force a predictable locale so OSC output isn't disturbed by
        // user-locale variations on test runners.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
        };
        if (environmentOverrides is not null)
        {
            foreach (var kv in environmentOverrides)
            {
                env[kv.Key] = kv.Value;
            }
        }

        var buffer = new TerminalBuffer(120, 30);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);

        var events = new List<OscEvent>();
        var capture = new StringBuilder();
        object sync = new();
        long lastOutputTicks = Environment.TickCount64;

        parser.OnPromptReady = () =>
        {
            lock (sync) events.Add(new OscEvent("A", null));
        };
        parser.OnCommandAccepted = text =>
        {
            // Re-encode to base64 so OscEvent.DecodedCommand round-trips
            // back to the same text -- matches the on-the-wire format the
            // bootstrap emits and keeps the OscEvent contract uniform.
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            lock (sync) events.Add(new OscEvent("C", b64));
        };
        parser.OnCommandFinishedDetailed = (exit, dur) =>
        {
            string payload = $"{exit?.ToString() ?? string.Empty};{dur?.ToString() ?? string.Empty}";
            lock (sync) events.Add(new OscEvent("D", payload));
        };
        parser.OnWorkingDirectoryChanged = cwd =>
        {
            // AnsiParser hands us the decoded path; re-prepend the scheme so
            // tests that look for Payload.StartsWith("file://") still match.
            lock (sync) events.Add(new OscEvent("7", "file://" + cwd));
        };

        var exitSignal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan timeoutSpan = timeout ?? TimeSpan.FromSeconds(15);

        var session = new RustPtySession(
            shellPath,
            cols: 120,
            rows: 30,
            args: arguments,
            cwd: null,
            skipPowerShellPostLaunchInit: true,
            environmentOverrides: env);

        session.OnOutputReceived += text =>
        {
            lock (sync) capture.Append(text);
            parser.Process(text);
            Volatile.Write(ref lastOutputTicks, Environment.TickCount64);
        };
        session.OnExit += code => exitSignal.TrySetResult(code);

        // Production TerminalPane wires parser responses (cursor-position
        // DSR replies, device-attribute replies, etc.) back to the session.
        // Without this, ConPTY can hang waiting on \x1B[6n at startup and
        // the child never gets to write anything.
        parser.OnResponse = resp => session.SendInput(resp);

        // RustPtySession.ReadLoop breaks out only when pty_read returns 0
        // (clean EOF). On Windows ConPTY the master read often errors out
        // (-1) after the child exits instead of returning 0, so ReadLoop
        // keeps retrying forever and OnExit never fires. Watch the actual
        // OS process by PID and signal exit ourselves once it's gone.
        var watcherCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            int? trackedPid = null;
            for (int i = 0; i < 30 && trackedPid is null; i++)
            {
                if (watcherCts.Token.IsCancellationRequested) return;
                trackedPid = session.Pid;
                if (trackedPid is null)
                {
                    try { await Task.Delay(100, watcherCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
            if (trackedPid is null) return;

            System.Diagnostics.Process p;
            try { p = System.Diagnostics.Process.GetProcessById(trackedPid.Value); }
            catch (ArgumentException) { exitSignal.TrySetResult(0); return; }
            using (p)
            {
                while (!watcherCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        p.Refresh();
                        if (p.HasExited)
                        {
                            exitSignal.TrySetResult(p.ExitCode);
                            return;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Process handle is gone -- treat as a clean exit.
                        exitSignal.TrySetResult(0);
                        return;
                    }
                    catch { /* transient query failure; retry next tick */ }
                    try { await Task.Delay(100, watcherCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }, watcherCts.Token);

        // Brief settle period before we feed scripted input. Zsh's
        // compinit-prompting issue on Ubuntu is already neutralized by
        // ZshShellIntegrationTests passing --no-global-rcs, so we don't
        // need a synchronous wait-for-prompt-ready here -- which adds
        // its own thread-scheduling sensitivity under CI parallelism.
        Thread.Sleep(200);

        // Interactive shells (bash -i, zsh -i, fish -i) put the TTY into
        // readline-style raw mode where the Enter key is CR (\r), not LF
        // (\n). Tests author scripted input as "echo hello\nexit 0\n" for
        // readability; translate LF -> CR here so each line is actually
        // submitted instead of sitting in the readline buffer forever.
        string ttyInput = scriptedStdin.Replace("\n", "\r");
        session.SendInput(ttyInput);

        bool exited;
        try
        {
            exited = exitSignal.Task.Wait(timeoutSpan);
            watcherCts.Cancel();

            // Belt-and-suspenders: if the shell hasn't terminated on its
            // own (timeout, or assertion-failure path where exitSignal
            // fires because we observed exit but the OS process is somehow
            // still around), kill it explicitly. On Linux, closing the
            // master fd in pty_close() does NOT unblock RustPtySession's
            // ReadLoop thread that's already blocked in pty_read on that
            // same fd; only the child closing its slave end produces the
            // EOF that lets ReadLoop terminate. Without this, a single
            // hung zsh `-i` cascades into the xunit host being unable to
            // shut down, which we observed as a 14-minute silent stall.
            try
            {
                int? pid = session.Pid;
                if (pid is int p)
                {
                    using var proc = System.Diagnostics.Process.GetProcessById(p);
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
            }
            catch { /* process already gone or unkillable -- nothing more we can do */ }
        }
        catch
        {
            session.Dispose();
            throw;
        }

        if (!exited)
        {
            string partial;
            int eventCount;
            lock (sync)
            {
                partial = capture.ToString();
                eventCount = events.Count;
            }
            session.Dispose();
            var dump = new StringBuilder(partial.Length * 2);
            foreach (char c in partial)
            {
                if (c >= 0x20 && c < 0x7F) dump.Append(c);
                else if (c == '\n') dump.Append("\\n");
                else if (c == '\r') dump.Append("\\r");
                else if (c == '\t') dump.Append("\\t");
                else dump.Append("\\x").Append(((int)c).ToString("X2"));
            }
            throw new TimeoutException(
                $"Shell {shellPath} did not exit within {timeoutSpan.TotalSeconds:F1}s. " +
                $"events={eventCount}, captured ({partial.Length} chars): {dump}");
        }

        // Drain window: the exit signal can fire from the PID watcher
        // before the PTY ReadLoop -> ProcessLoop -> parser pipeline has
        // delivered the last few bytes (which may contain the final OSC
        // 133;C / D markers). Sleeping a fixed N ms is a race on a loaded
        // CI runner; instead wait until OnOutputReceived has been quiet
        // for `drainIdleMs` consecutive ms, capped by `drainCapMs` overall
        // so we don't hang forever if the shell decides to keep printing
        // after the child reaps.
        const long drainIdleMs = 200;
        const long drainCapMs = 2000;
        long drainStart = Environment.TickCount64;
        while (true)
        {
            long now = Environment.TickCount64;
            long sinceOutput = now - Volatile.Read(ref lastOutputTicks);
            long sinceDrainStart = now - drainStart;
            if (sinceOutput >= drainIdleMs || sinceDrainStart >= drainCapMs) break;
            Thread.Sleep(25);
        }

        string captured;
        OscEvent[] eventsSnapshot;
        lock (sync)
        {
            captured = capture.ToString();
            eventsSnapshot = events.ToArray();
        }
        session.Dispose();

        // PTY merges stdout and stderr onto the same master fd, so there is
        // no way to separate the two streams here. Expose the combined
        // capture under both fields: the existing error-pattern tests look
        // for specific words ("command not found", "syntax error", "%N",
        // etc.) which don't false-positive against normal PS1/echo traffic.
        return new HarnessResult(captured, captured, exitSignal.Task.Result, eventsSnapshot);
    }
}
