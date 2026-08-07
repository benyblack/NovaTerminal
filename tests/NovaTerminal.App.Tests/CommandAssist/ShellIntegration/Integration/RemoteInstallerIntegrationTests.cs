using System.Diagnostics;
using System.Text;
using NovaTerminal.CommandAssist.ShellIntegration.Remote;

namespace NovaTerminal.Tests.CommandAssist.ShellIntegration.Integration;

/// <summary>
/// The generated one-liner, run the way a user pastes it: through a real bash, with
/// <c>HOME</c> redirected to a temp directory.
/// </summary>
/// <remarks>
/// <para>
/// RemoteShellIntegrationInstallerTests asserts on the command's text, which cannot see the bugs
/// this file was written for: a heredoc that drops a line, an rc file patched twice, a
/// <c>grep -q</c> marker that misses a hand-placed loader line, or a decode failure that writes an
/// empty snippet and reports success.
/// </para>
/// <para>
/// The installer itself needs no TTY, so it runs under <c>bash -c</c> via
/// <see cref="Process"/>. Only the last test - "and afterwards the marks actually flow" - needs an
/// interactive shell, and that one goes through <see cref="ShellHarness"/> on a real PTY.
/// </para>
/// <para>
/// Skipped when bash is absent. <c>HOME</c> is a per-test temp directory so the developer's own
/// dotfiles are never touched, and it is passed with forward slashes because Git Bash resolves
/// <c>$HOME/...</c> more predictably that way.
/// </para>
/// </remarks>
[Trait("Category", "ShellIntegration")]
[Collection(nameof(ShellIntegrationCollection))]
public sealed class RemoteInstallerIntegrationTests : IDisposable
{
    private readonly string _home;

    public RemoteInstallerIntegrationTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"nova_installer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private string HomeForShell => _home.Replace('\\', '/');

    private string SnippetPath => Path.Combine(_home, ".nova-shell-integration.sh");

    private string BashrcPath => Path.Combine(_home, ".bashrc");

    /// <summary>Runs the pasted one-liner under a non-interactive bash and returns its output.</summary>
    /// <param name="pathOverride">
    /// Replaces <c>PATH</c> for the child. On real Linux bash this alone hides <c>base64</c> and
    /// <c>gzip</c>; kept for that platform even though it is not sufficient on Git Bash - see
    /// <paramref name="shadowDecodeTools"/>.
    /// </param>
    /// <param name="shadowDecodeTools">
    /// Prepends bash functions named <c>base64</c> and <c>gzip</c> that both fail, shadowing the
    /// real commands regardless of <c>PATH</c>. Needed because Git Bash's MSYS runtime injects
    /// <c>/mingw64/bin:/usr/bin</c> at the front of <c>PATH</c> unconditionally - confirmed with
    /// <c>PATH=/nonexistent bash.exe --noprofile --norc -c 'echo $PATH'</c> still reporting
    /// <c>/mingw64/bin:/usr/bin:...</c> first - so an empty-directory <c>PATH</c> override alone
    /// never hides <c>base64</c>/<c>gzip</c> on this platform and the installer always "succeeds".
    /// A bash function takes precedence over a <c>PATH</c> lookup for a simple command, which is
    /// what lets this actually exercise the failure branch on Windows.
    /// </param>
    private string RunInstaller(string? pathOverride = null, bool shadowDecodeTools = false)
    {
        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.BashOrZsh);
        if (shadowDecodeTools)
        {
            command = "base64() { return 1; }; gzip() { return 1; }; " + command;
        }

        var startInfo = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        startInfo.Environment["HOME"] = HomeForShell;
        if (pathOverride is not null)
        {
            startInfo.Environment["PATH"] = pathOverride;
        }

        using Process process = Process.Start(startInfo)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "installer did not finish within 30s");

        return stdout + stderr;
    }

    private static int CountLoaderLines(string rcContent) => rcContent
        .Split('\n')
        .Count(line => line.Contains("nova-shell-integration", StringComparison.Ordinal));

    // ---- the happy path -------------------------------------------------------------------------

    [Fact]
    public void Installer_WritesTheSnippetByteForByte()
    {
        string output = RunInstaller();

        Assert.True(File.Exists(SnippetPath), $"snippet not written. output:\n{output}");
        Assert.Equal(
            RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.BashOrZsh).TrimEnd('\n'),
            File.ReadAllText(SnippetPath).Replace("\r\n", "\n").TrimEnd('\n'));
        Assert.Contains("nova: wrote ~/.nova-shell-integration.sh", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rc edit is the step users forget, so the installer does it - and it detects bash from the
    /// <c>${BASH_VERSION:+bash}</c> the live shell expands into the child's argv, with nothing
    /// sourced.
    /// </summary>
    [Fact]
    public void Installer_AddsTheLoaderLineToBashrc()
    {
        string output = RunInstaller();

        Assert.True(File.Exists(BashrcPath), $"~/.bashrc not created. output:\n{output}");
        string rc = File.ReadAllText(BashrcPath);
        Assert.Contains(
            RemoteShellIntegrationSnippets.GetLoaderLine(RemoteShellIntegrationShell.BashOrZsh)!,
            rc,
            StringComparison.Ordinal);
        Assert.Contains("nova: added loader line to ~/.bashrc", output, StringComparison.Ordinal);
    }

    // ---- idempotency ----------------------------------------------------------------------------

    [Fact]
    public void Installer_RunTwice_LeavesExactlyOneLoaderLine()
    {
        RunInstaller();
        string secondOutput = RunInstaller();

        Assert.Equal(1, CountLoaderLines(File.ReadAllText(BashrcPath)));
        Assert.Contains("already present", secondOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A user who followed the old docs already has the loader line. The marker is the file name
    /// rather than our exact line, so a hand-typed variant is still recognized.
    /// </summary>
    [Fact]
    public void Installer_HandPlacedLoaderLine_IsNotDuplicated()
    {
        File.WriteAllText(
            BashrcPath,
            "PS1='test$ '\nsource ~/.nova-shell-integration.sh\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        RunInstaller();

        Assert.Equal(1, CountLoaderLines(File.ReadAllText(BashrcPath)));
    }

    /// <summary>
    /// A `.bashrc` with no trailing newline is common (many editors don't add one). The append must
    /// not land on the same line as the user's last line - it must first ensure the file ends in a
    /// newline, then add the loader on its own line. A regression here breaks the user's last rc line
    /// AND leaves the loader unreadable by the shell, on a remote host, while the installer still
    /// reports success.
    /// </summary>
    [Fact]
    public void Installer_BashrcWithoutTrailingNewline_PreservesLastLineAndAddsLoaderOnItsOwnLine()
    {
        File.WriteAllText(
            BashrcPath,
            "export FOO=bar",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string output = RunInstaller();

        string expectedLoader = RemoteShellIntegrationSnippets.GetLoaderLine(RemoteShellIntegrationShell.BashOrZsh)!;
        string rc = File.ReadAllText(BashrcPath).Replace("\r\n", "\n");
        Assert.Equal($"export FOO=bar\n{expectedLoader}\n", rc);
        Assert.Equal(1, CountLoaderLines(rc));
        Assert.Contains("nova: added loader line to ~/.bashrc", output, StringComparison.Ordinal);
    }

    // ---- shell selection -------------------------------------------------------------------------

    /// <summary>
    /// Every other behavioural test drives the bash arm via <c>${BASH_VERSION:+bash}</c>. This test
    /// invokes the installer directly with an explicit "zsh" argument (the same technique
    /// <see cref="FishInstaller_WritesTheSnippetIntoConfD"/> uses for fish) so the <c>zsh)</c> case
    /// arm is actually exercised: it must patch <c>~/.zshrc</c>, not <c>~/.bashrc</c>.
    /// </summary>
    [Fact]
    public void Installer_WithZshArgument_PatchesZshrcNotBashrc()
    {
        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        string installer = RemoteShellIntegrationSnippets.BuildInstallerScript(
            RemoteShellIntegrationShell.BashOrZsh);
        string installerPath = Path.Combine(_home, "nova-install.sh");
        File.WriteAllText(installerPath, installer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(installerPath.Replace('\\', '/'));
        startInfo.ArgumentList.Add("zsh");
        startInfo.Environment["HOME"] = HomeForShell;

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "installer did not finish within 30s");

        string zshrcPath = Path.Combine(_home, ".zshrc");
        Assert.True(File.Exists(zshrcPath), $"~/.zshrc not created. output:\n{output}");
        Assert.False(File.Exists(BashrcPath), "~/.bashrc should not be touched when zsh is selected");
        Assert.Contains(
            RemoteShellIntegrationSnippets.GetLoaderLine(RemoteShellIntegrationShell.BashOrZsh)!,
            File.ReadAllText(zshrcPath),
            StringComparison.Ordinal);
        Assert.Contains("nova: added loader line to ~/.zshrc", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>*)</c> arm: no argument and an unrecognized/unset <c>$SHELL</c>. The installer cannot
    /// silently do nothing here - it must tell the user it could not tell which shell they use and
    /// print the loader line for them to add by hand, and it must not create either rc file.
    /// </summary>
    [Fact]
    public void Installer_WithUnrecognizedShell_TellsTheUserAndPrintsTheLoaderLine()
    {
        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        string installer = RemoteShellIntegrationSnippets.BuildInstallerScript(
            RemoteShellIntegrationShell.BashOrZsh);
        string installerPath = Path.Combine(_home, "nova-install.sh");
        File.WriteAllText(installerPath, installer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(installerPath.Replace('\\', '/'));
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.Environment["HOME"] = HomeForShell;
        // Override whatever $SHELL the test machine happens to have so the fallback resolves to
        // something the case statement's zsh)/bash) arms cannot match, deterministically reaching *).
        startInfo.Environment["SHELL"] = "/bin/nonsense-shell";

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "installer did not finish within 30s");

        Assert.Contains("nova: could not tell which shell you use", output, StringComparison.Ordinal);
        Assert.Contains(
            RemoteShellIntegrationSnippets.GetLoaderLine(RemoteShellIntegrationShell.BashOrZsh)!,
            output,
            StringComparison.Ordinal);
        Assert.False(File.Exists(BashrcPath), "~/.bashrc should not be created when the shell is unknown");
        Assert.False(
            File.Exists(Path.Combine(_home, ".zshrc")),
            "~/.zshrc should not be created when the shell is unknown");
    }

    // ---- failure ---------------------------------------------------------------------------------

    /// <summary>
    /// With no <c>base64</c> or <c>gzip</c> reachable the decode produces an empty temp file. The
    /// one-liner must say so rather than silently install nothing, which is the failure mode a user
    /// cannot diagnose.
    /// </summary>
    /// <remarks>
    /// An empty-directory <c>PATH</c> override is not enough here: Git Bash's MSYS runtime
    /// unconditionally prepends <c>/mingw64/bin:/usr/bin</c> to <c>PATH</c> before the script sees
    /// it (still true with <c>--noprofile --norc</c>, so it is not a sourced file), which means
    /// <c>base64</c>/<c>gzip</c> are always found there regardless of what this test passes as
    /// <c>PATH</c> - the installer "succeeds" and this test's whole premise silently fails to hold.
    /// <see cref="RunInstaller"/>'s <c>shadowDecodeTools</c> additionally defines bash functions
    /// named <c>base64</c>/<c>gzip</c> that fail, which take precedence over any <c>PATH</c> lookup
    /// and so reach the failure branch on every platform.
    /// </remarks>
    [Fact]
    public void Installer_WithoutBase64OrGzip_ReportsFailureAndWritesNothing()
    {
        string emptyDir = Path.Combine(_home, "empty-path");
        Directory.CreateDirectory(emptyDir);

        string output = RunInstaller(pathOverride: emptyDir.Replace('\\', '/'), shadowDecodeTools: true);

        Assert.Contains("nova: install failed", output, StringComparison.Ordinal);
        Assert.False(File.Exists(SnippetPath), "snippet written despite a failed decode");
    }

    // ---- and afterwards, the marks flow ----------------------------------------------------------

    /// <summary>
    /// The end-to-end claim: install, then start an interactive bash that reads the rc file the
    /// installer patched, and the OSC 133 lifecycle arrives. This is what the user gets on their
    /// next session, and it is asserted through the production PTY + parser path.
    /// </summary>
    [Fact]
    public void AfterInstalling_ANewInteractiveShell_EmitsTheLifecycle()
    {
        RunInstaller();

        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        // The installer writes only the loader line; a prompt is needed for the 133;B mark to have
        // somewhere to land, so prepend one the same way RemoteBashSnippetIntegrationTests does.
        string rc = File.ReadAllText(BashrcPath);
        File.WriteAllText(
            BashrcPath,
            "PS1='nova-test$ '\n" + rc,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var env = new Dictionary<string, string> { ["HOME"] = HomeForShell };
        HarnessResult result = ShellHarness.Run(
            bash,
            $"--rcfile \"{BashrcPath.Replace('\\', '/')}\" -i",
            "echo hello\nexit 0\n",
            env,
            TimeSpan.FromSeconds(20));

        Assert.Contains(result.Events, e => e.Kind == "A");
        Assert.Contains(result.Events, e => e.Kind == "B");
        Assert.Contains(
            result.Events.Where(e => e.Kind == "C").Select(e => e.DecodedCommand),
            t => t == "echo hello");
        Assert.Contains(result.Events, e => e.Kind == "D" && e.DecodedFinish.exitCode == 0);
    }

    // ---- fish -------------------------------------------------------------------------------------

    /// <summary>
    /// The fish installer is POSIX sh, so it is exercised under bash: the payload it writes is fish
    /// content, but nothing about running the installer needs fish present. That the fish snippet
    /// itself works is FishShellIntegrationTests' job.
    /// </summary>
    [Fact]
    public void FishInstaller_WritesTheSnippetIntoConfD()
    {
        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        // Run the fish installer's payload directly: the fish one-liner is fish syntax, which bash
        // cannot parse, and what is under test here is the installer it decodes to.
        string installer = RemoteShellIntegrationSnippets.BuildInstallerScript(
            RemoteShellIntegrationShell.Fish);
        string installerPath = Path.Combine(_home, "nova-install-fish.sh");
        File.WriteAllText(installerPath, installer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(installerPath.Replace('\\', '/'));
        startInfo.ArgumentList.Add("fish");
        startInfo.Environment["HOME"] = HomeForShell;

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "fish installer did not finish within 30s");

        string dest = Path.Combine(_home, ".config", "fish", "conf.d", "nova-shell-integration.fish");
        Assert.True(File.Exists(dest), $"fish snippet not written. output:\n{output}");
        Assert.Equal(
            RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish).TrimEnd('\n'),
            File.ReadAllText(dest).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    /// <summary>
    /// FishInstaller_WritesTheSnippetIntoConfD deliberately runs the *installer* (POSIX sh) under
    /// bash, because the installer itself is sh - but that means nothing in the suite exercises the
    /// fish one-liner *wrapper* (<c>set -l __nova_t (mktemp); ...</c>) through a real fish. A bad
    /// quote, an operator precedence slip, or an accidental <c>$(...)</c> where <c>(...)</c> is
    /// required would ship undetected. This test runs the actual generated one-liner through
    /// <c>fish -c</c>, mirroring <see cref="RunInstaller"/>'s bash equivalent.
    /// </summary>
    [Fact]
    public void FishOneLiner_RunsUnderRealFish()
    {
        string? fish = ShellHarness.FindFish();
        if (fish is null)
        {
            Assert.Skip("fish not found on this system");
        }

        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.Fish);

        var startInfo = new ProcessStartInfo(fish)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        startInfo.Environment["HOME"] = HomeForShell;

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "fish one-liner did not finish within 30s");

        string dest = Path.Combine(_home, ".config", "fish", "conf.d", "nova-shell-integration.fish");
        Assert.True(File.Exists(dest), $"fish snippet not written. output:\n{output}");
        Assert.Equal(
            RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.Fish).TrimEnd('\n'),
            File.ReadAllText(dest).Replace("\r\n", "\n").TrimEnd('\n'));
        Assert.Contains("nova:", output, StringComparison.Ordinal);
    }

    // ---- the live shell is untouched -------------------------------------------------------------

    /// <summary>
    /// The design's central promise: the installer runs as a child, so nothing it defines can reach
    /// the shell that pasted the line. Asserted by checking the calling shell afterwards for the
    /// installer's own variables, <c>__nova_dest</c> and <c>__nova_t</c> - not the snippet, which this
    /// probe does not source and so cannot say anything about.
    /// </summary>
    [Fact]
    public void Installer_LeavesNothingBehindInTheCallingShell()
    {
        string? bash = ShellHarness.FindBash();
        if (bash is null)
        {
            Assert.Skip("bash not found on this system");
        }

        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.BashOrZsh);
        string probe =
            command +
            "; echo \"probe-dest=[${__nova_dest-}]\"" +
            "; echo \"probe-temp=[${__nova_t-}]\"";

        var startInfo = new ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(probe);
        startInfo.Environment["HOME"] = HomeForShell;

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "probe did not finish within 30s");

        Assert.Contains("probe-dest=[]", output, StringComparison.Ordinal);
        Assert.Contains("probe-temp=[]", output, StringComparison.Ordinal);
    }

    // ---- powershell ---------------------------------------------------------------------------

    /// <summary>
    /// The PowerShell installer, run by a real pwsh with both of its parameters redirected into the
    /// temp HOME. The parameters are the only reason this is testable: <c>$PROFILE</c> resolves
    /// under the developer's Documents directory and cannot be redirected by an environment variable.
    /// </summary>
    [Fact]
    public void PowerShellInstaller_WritesTheSnippetAndPatchesTheProfileOnce()
    {
        string? pwsh = FindPwsh();
        if (pwsh is null)
        {
            Assert.Skip("pwsh not found on this system");
        }

        string installerPath = Path.Combine(_home, "nova-install.ps1");
        File.WriteAllText(
            installerPath,
            RemoteShellIntegrationSnippets.BuildInstallerScript(
                RemoteShellIntegrationShell.PowerShell),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string profilePath = Path.Combine(_home, "profile.ps1");
        string output = RunPwsh(pwsh, installerPath, profilePath) + RunPwsh(pwsh, installerPath, profilePath);

        string dest = Path.Combine(_home, ".nova-shell-integration.ps1");
        Assert.True(File.Exists(dest), $"snippet not written. output:\n{output}");
        Assert.Equal(
            RemoteShellIntegrationSnippets.Read(RemoteShellIntegrationShell.PowerShell).TrimEnd('\n'),
            File.ReadAllText(dest).Replace("\r\n", "\n").TrimEnd('\n'));
        Assert.Equal(1, CountLoaderLines(File.ReadAllText(profilePath)));
        Assert.Contains("already present", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The PowerShell equivalent of
    /// <see cref="Installer_BashrcWithoutTrailingNewline_PreservesLastLineAndAddsLoaderOnItsOwnLine"/>:
    /// <c>Add-Content</c> against a profile that does not end in a newline concatenates the loader
    /// onto the user's last line instead of appending it as its own line.
    /// </summary>
    [Fact]
    public void PowerShellInstaller_ProfileWithoutTrailingNewline_PreservesLastLineAndAddsLoaderOnItsOwnLine()
    {
        string? pwsh = FindPwsh();
        if (pwsh is null)
        {
            Assert.Skip("pwsh not found on this system");
        }

        string installerPath = Path.Combine(_home, "nova-install.ps1");
        File.WriteAllText(
            installerPath,
            RemoteShellIntegrationSnippets.BuildInstallerScript(
                RemoteShellIntegrationShell.PowerShell),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string profilePath = Path.Combine(_home, "profile.ps1");
        File.WriteAllText(profilePath, "$x = 1", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string output = RunPwsh(pwsh, installerPath, profilePath);

        string expectedLoader = RemoteShellIntegrationSnippets.GetLoaderLine(RemoteShellIntegrationShell.PowerShell)!;
        string profile = File.ReadAllText(profilePath).Replace("\r\n", "\n");
        Assert.Equal($"$x = 1\n{expectedLoader}\n", profile);
        Assert.Contains("nova: added loader line", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="PowerShellInstaller_WritesTheSnippetAndPatchesTheProfileOnce"/> invokes the
    /// installer via <c>-File</c>, which parses the script as its own document and never exercises
    /// the generated one-liner's own syntax - the wrapper that decodes and runs it. This parses (but
    /// does not execute, to avoid touching the developer's real <c>$PROFILE</c>) the actual generated
    /// <see cref="RemoteShellIntegrationSnippets.BuildInstallerCommand"/> string for the PowerShell
    /// shell through <see cref="System.Management.Automation.Language.Parser.ParseInput"/> in a real
    /// pwsh, so a quoting or precedence slip in the wrapper template would fail here.
    /// </summary>
    [Fact]
    public void PowerShellOneLiner_ParsesWithoutErrors()
    {
        string? pwsh = FindPwsh();
        if (pwsh is null)
        {
            Assert.Skip("pwsh not found on this system");
        }

        string command = RemoteShellIntegrationSnippets.BuildInstallerCommand(
            RemoteShellIntegrationShell.PowerShell);
        string commandPath = Path.Combine(_home, "nova-install-oneliner.txt");
        File.WriteAllText(commandPath, command, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string parseScript =
            "$__cmd = Get-Content -LiteralPath '" + commandPath.Replace('\\', '/') + "' -Raw\n" +
            "$__tokens = $null\n" +
            "$__errors = $null\n" +
            "[void][System.Management.Automation.Language.Parser]::ParseInput($__cmd, [ref]$__tokens, [ref]$__errors)\n" +
            "if ($__errors.Count -gt 0) {\n" +
            "    $__errors | ForEach-Object { [Console]::Error.WriteLine($_.ToString()) }\n" +
            "    exit 1\n" +
            "} else {\n" +
            "    exit 0\n" +
            "}\n";

        var startInfo = new ProcessStartInfo(pwsh)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(parseScript);

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "parse check did not finish within 30s");
        Assert.True(process.ExitCode == 0, $"one-liner failed to parse:\n{output}\ncommand:\n{command}");
    }

    private static string? FindPwsh()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null) return null;
        string exe = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private string RunPwsh(string pwsh, string installerPath, string profilePath)
    {
        var startInfo = new ProcessStartInfo(pwsh)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("-ProfilePath");
        startInfo.ArgumentList.Add(profilePath);
        startInfo.ArgumentList.Add("-DestDir");
        startInfo.ArgumentList.Add(_home);

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "pwsh installer did not finish within 60s");
        return output;
    }
}
