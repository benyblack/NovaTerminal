using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NovaTerminal.Conformance;

namespace NovaTerminal.Tests;

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
        Assert.Contains("NovaTerminal VT Report", output);
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
        string cliProjectPath = Path.Combine(repoRoot, "src", "NovaTerminal.Cli", "NovaTerminal.Cli.csproj");
        string cliExecutablePath = GetExecutablePath(Path.Combine(repoRoot, "src", "NovaTerminal.Cli", "bin", "Release", "net10.0"), "NovaTerminal.Cli");
        (int buildExitCode, string buildStdOut, string buildStdErr) = RunProcessFromRepository(
            repoRoot,
            "dotnet",
            $"build \"{cliProjectPath}\" -c Release --no-restore -p:BuildProjectReferences=false");
        (int exitCode, string stdout, string stderr) = RunProcessFromRepository(
            repoRoot,
            cliExecutablePath,
            "--vt-report");

        Assert.Equal(0, buildExitCode);
        Assert.Equal(string.Empty, buildStdErr);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("NovaTerminal VT Report", stdout);
        Assert.Contains("Matrix:", stdout);
        Assert.Contains("Validation:", stdout);
    }

    [Fact]
    public void AppBinary_PrintsHumanReadableSummary()
    {
        string repoRoot = FindRepositoryRoot();
        string appProjectPath = Path.Combine(repoRoot, "src", "NovaTerminal.App", "NovaTerminal.App.csproj");
        string appExecutablePath = GetExecutablePath(Path.Combine(repoRoot, "src", "NovaTerminal.App", "bin", "Release", "net10.0"), "NovaTerminal");
        (int buildExitCode, string buildStdOut, string buildStdErr) = RunProcessFromRepository(
            repoRoot,
            "dotnet",
            $"build \"{appProjectPath}\" -c Release --no-restore");
        (int exitCode, string stdout, string stderr) = RunProcessFromRepository(
            repoRoot,
            appExecutablePath,
            "--vt-report");

        Assert.Equal(0, buildExitCode);
        Assert.Equal(string.Empty, buildStdErr);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("NovaTerminal VT Report", stdout);
        Assert.Contains("Matrix:", stdout);
        Assert.Contains("Validation:", stdout);
    }

    [Fact]
    public void AppBuild_CopiesCliShim_AsNamedSidecar()
    {
        string repoRoot = FindRepositoryRoot();
        string appProjectPath = Path.Combine(repoRoot, "src", "NovaTerminal.App", "NovaTerminal.App.csproj");
        string appOutputDirectory = Path.Combine(repoRoot, "src", "NovaTerminal.App", "bin", "Release", "net10.0");
        (int buildExitCode, string buildStdOut, string buildStdErr) = RunProcessFromRepository(
            repoRoot,
            "dotnet",
            $"build \"{appProjectPath}\" -c Release --no-restore");

        Assert.Equal(0, buildExitCode);
        Assert.Equal(string.Empty, buildStdErr);
        Assert.True(File.Exists(GetExecutablePath(appOutputDirectory, "NovaTerminal")));
        Assert.True(File.Exists(GetExecutablePath(appOutputDirectory, "NovaTerminal.Cli")));
        Assert.False(File.Exists(GetExecutablePath(appOutputDirectory, "NovaTerminal.Gui")));
    }

    [Fact]
    public void ShippedArtifact_MatchesFreshToolOutput()
    {
        string repoRoot = FindRepositoryRoot();
        string artifactPath = Path.Combine(repoRoot, "src", "NovaTerminal.App", "Resources", "vt-conformance-report.json");
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
            if (File.Exists(Path.Combine(directory.FullName, "NovaTerminal.sln")))
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

        string stdout = process!.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }

    private static string GetExecutablePath(string directory, string baseName)
    {
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{baseName}.exe"
            : baseName;
        return Path.Combine(directory, fileName);
    }

}
