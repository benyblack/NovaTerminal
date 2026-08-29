using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NovaTerminal.Shots;

/// <summary>Output directory, scale, and the manifest accumulated across one invocation.</summary>
public sealed class ShotRun
{
    private readonly List<ShotAsset> _assets = [];

    // Scenario names, not a count: Publisher.Prune's clip-degradation guard (see its remarks)
    // needs to name them in its refusal, the same way Program's own failedScenarios list does
    // for a per-scenario exception. This is the *sanctioned* degradation path - ffmpeg missing
    // from PATH - which the spec deliberately does not treat as a failure (ShotContext.RecordAsync
    // keeps the frames and returns normally rather than throwing), so it cannot live on the same
    // list as an actual thrown exception without collapsing two different remedies ("install
    // ffmpeg" vs. "fix the scenario and re-run") into one message.
    private readonly List<string> _clipEncodingSkippedFor = [];

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

    /// <summary>
    /// Scenarios whose <see cref="ShotContext.RecordAsync"/> call kept its captured frames but
    /// skipped WebM/GIF encoding because ffmpeg was not on <c>PATH</c> at the time - see
    /// <see cref="RecordClipEncodingSkipped"/>.
    /// </summary>
    public IReadOnlyList<string> ClipEncodingSkippedFor => _clipEncodingSkippedFor;

    public void Record(ShotAsset asset) => _assets.Add(asset);

    /// <summary>
    /// Records that <paramref name="scenarioName"/>'s clip encoding was skipped this run because
    /// ffmpeg was unavailable - a sanctioned degradation (the run still succeeds, per the spec),
    /// not a failure, but one <see cref="Publisher.Prune"/> must still refuse to treat as "this
    /// scenario no longer produces a clip" the same way it must refuse an actual thrown exception:
    /// in both cases the scenario's previously-committed <c>.webm</c>/<c>.gif</c> would otherwise
    /// look indistinguishable from a deliberate removal.
    /// </summary>
    public void RecordClipEncodingSkipped(string scenarioName) => _clipEncodingSkippedFor.Add(scenarioName);

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
