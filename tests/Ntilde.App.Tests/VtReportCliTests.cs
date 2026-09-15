using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ntilde.Conformance;

namespace Ntilde.Tests;

public sealed class VtReportCliTests
{
    [Fact]
    public void IsSupportedCliMode_ReturnsFalse_WhenFlagIsMissing()
    {
        Assert.False(VtReportCommand.IsSupportedCliMode(["--help"]));
    }

    [Fact]
    public void IsSupportedCliMode_ReturnsTrue_ForVtReport()
    {
        Assert.True(VtReportCommand.IsSupportedCliMode(["--vt-report"]));
    }

    [Fact]
    public void Execute_PrintsHumanReadableSummary_ByDefault()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = VtReportCommand.Execute(["--vt-report"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        string output = stdout.ToString();
        Assert.Contains("Ntilde VT Report", output);
        Assert.Contains("Matrix:", output);
        Assert.Contains("Supported:", output);
        Assert.Contains("Validation:", output);
        Assert.Contains("--vt-report --json", output);
    }

    [Fact]
    public void Execute_PrintsEmbeddedJson_WhenJsonFlagIsPresent()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = VtReportCommand.Execute(["--vt-report", "--json"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        string output = stdout.ToString().Trim();
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.True(document.RootElement.TryGetProperty("summary", out _));
        Assert.True(document.RootElement.TryGetProperty("rows", out _));
        Assert.True(document.RootElement.TryGetProperty("warnings", out _));
    }

    [Fact]
    public void TryRun_ReturnsFalse_WhenFlagIsMissing()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        bool handled = VtReportCli.TryRun(["--help"], stdout, stderr, out int exitCode);

        Assert.False(handled);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void CliShim_PrintsHumanReadableSummary()
    {
        string repoRoot = FindRepositoryRoot();
        string cliProjectPath = Path.Combine(repoRoot, "src", "Ntilde.Cli", "Ntilde.Cli.csproj");
        string cliExecutablePath = GetExecutablePath(Path.Combine(repoRoot, "src", "Ntilde.Cli", "bin", "Release", "net10.0"), "Ntilde.Cli");
        // Builds the references too: with -p:BuildProjectReferences=false the compiler wants
        // src/*/obj/Release/net10.0/ref/*.dll, which on CI only exist if AppBinary_* ran first.
        // xunit orders cases by a name hash, so that dependency flipped when the namespace changed.
        (int buildExitCode, string buildStdOut, string buildStdErr) = RunProcessFromRepository(
            repoRoot,
            "dotnet",
            $"build \"{cliProjectPath}\" -c Release --no-restore -nodeReuse:false");
        (int exitCode, string stdout, string stderr) = RunProcessFromRepository(
            repoRoot,
            cliExecutablePath,
            "--vt-report");

        Assert.True(buildExitCode == 0, $"dotnet build exited {buildExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{buildStdOut}{Environment.NewLine}stderr:{Environment.NewLine}{buildStdErr}");
        Assert.Equal(string.Empty, buildStdErr);
        Assert.True(exitCode == 0, $"process exited {exitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Ntilde VT Report", stdout);
        Assert.Contains("Matrix:", stdout);
        Assert.Contains("Validation:", stdout);
    }

    [Fact]
    public void AppBinary_PrintsHumanReadableSummary()
    {
        string repoRoot = FindRepositoryRoot();
        string appProjectPath = Path.Combine(repoRoot, "src", "Ntilde.App", "Ntilde.App.csproj");
        string appExecutablePath = GetExecutablePath(Path.Combine(repoRoot, "src", "Ntilde.App", "bin", "Release", "net10.0"), "Ntilde");
        (int buildExitCode, string buildStdOut, string buildStdErr) = RunProcessFromRepository(
            repoRoot,
            "dotnet",
            $"build \"{appProjectPath}\" -c Release --no-restore -nodeReuse:false");
        (int exitCode, string stdout, string stderr) = RunProcessFromRepository(
            repoRoot,
            appExecutablePath,
            "--vt-report");

        Assert.True(buildExitCode == 0, $"dotnet build exited {buildExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{buildStdOut}{Environment.NewLine}stderr:{Environment.NewLine}{buildStdErr}");
        Assert.Equal(string.Empty, buildStdErr);
        Assert.True(exitCode == 0, $"process exited {exitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Ntilde VT Report", stdout);
        Assert.Contains("Matrix:", stdout);
        Assert.Contains("Validation:", stdout);
    }

    [Fact]
    public void AppBuild_CopiesCliShim_AsNamedSidecar()
    {
        string repoRoot = FindRepositoryRoot();
        string appProjectPath = Path.Combine(repoRoot, "src", "Ntilde.App", "Ntilde.App.csproj");
        string appOutputDirectory = Path.Combine(repoRoot, "src", "Ntilde.App", "bin", "Release", "net10.0");
        (int buildExitCode, string buildStdOut, string buildStdErr) = RunProcessFromRepository(
            repoRoot,
            "dotnet",
            $"build \"{appProjectPath}\" -c Release --no-restore -nodeReuse:false");

        Assert.True(buildExitCode == 0, $"dotnet build exited {buildExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{buildStdOut}{Environment.NewLine}stderr:{Environment.NewLine}{buildStdErr}");
        Assert.Equal(string.Empty, buildStdErr);
        Assert.True(File.Exists(GetExecutablePath(appOutputDirectory, "Ntilde")));
        Assert.True(File.Exists(GetExecutablePath(appOutputDirectory, "Ntilde.Cli")));
        Assert.False(File.Exists(GetExecutablePath(appOutputDirectory, "Ntilde.Gui")));
    }

    [Fact]
    public void ShippedArtifact_MatchesFreshToolOutput()
    {
        string repoRoot = FindRepositoryRoot();
        string artifactPath = Path.Combine(repoRoot, "src", "Ntilde.App", "Resources", "vt-conformance-report.json");
        string expected = VtConformanceReportTool.Serialize(
            VtConformanceReportTool.Generate(repoRoot, Path.Combine(repoRoot, "docs", "vt_coverage_matrix.md")));
        string actual = File.ReadAllText(artifactPath);

        Assert.Equal(expected, actual);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ntilde.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcessFromRepository(
        string repoRoot,
        string fileName,
        string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        // Read stdout and stderr concurrently to avoid the deadlock that occurs
        // on Windows when the subprocess fills the 4KB stderr pipe buffer while
        // we're blocked on ReadToEnd() for stdout.
        //
        // Tests that invoke `dotnet build` here must pass `-nodeReuse:false`,
        // otherwise the MSBuild worker pool outlives the build process and holds
        // the write end of the redirected pipes — WaitForExit() returns but
        // ReadToEnd() never sees EOF, causing the test (and Linux CI) to hang.
        var stdoutTask = Task.Run(() => process!.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => process!.StandardError.ReadToEnd());
        process!.WaitForExit();

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string GetExecutablePath(string directory, string baseName)
    {
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{baseName}.exe"
            : baseName;
        return Path.Combine(directory, fileName);
    }

}
