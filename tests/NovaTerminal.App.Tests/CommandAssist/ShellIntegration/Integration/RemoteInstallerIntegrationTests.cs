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

    // ---- the live shell is untouched -------------------------------------------------------------

    /// <summary>
    /// The design's central promise: the installer runs as a child, so nothing it defines can reach
    /// the shell that pasted the line. Asserted by checking the calling shell afterwards for the
    /// installer's own variables and for the snippet's marker function.
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
}
