using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NovaTerminal.Shots;

/// <summary>Output directory, scale, and the manifest accumulated across one invocation.</summary>
public sealed class ShotRun
{
    private readonly List<ShotAsset> _assets = [];

    public ShotRun(string outputDirectory, double scale)
    {
        OutputDirectory = outputDirectory;
        Scale = scale;
        Commit = ReadCommit();
        Os = RuntimeInformation.RuntimeIdentifier;
    }

    public string OutputDirectory { get; }

    public double Scale { get; }

    public string Commit { get; }

    public string Os { get; }

    public IReadOnlyList<ShotAsset> Assets => _assets;

    public void Record(ShotAsset asset) => _assets.Add(asset);

    public void WriteManifest() =>
        Manifest.Write(Path.Combine(OutputDirectory, "shots.json"), _assets);

    private static string ReadCommit()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using Process? process = Process.Start(psi);
            return process?.StandardOutput.ReadToEnd().Trim() ?? "unknown";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }
}
