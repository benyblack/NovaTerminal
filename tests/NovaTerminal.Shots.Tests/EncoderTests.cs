using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class EncoderTests
{
    /// <summary>
    /// Encoder.IsAvailable() must agree with an independent probe for ffmpeg on PATH. A hard
    /// assert that ffmpeg exists would fail this suite in CI, which has no ffmpeg installed
    /// (grep -n ffmpeg .github/workflows/ci.yml returns nothing) and whose absence the spec
    /// says should degrade a run to stills-only rather than fail it. So this test is skipped
    /// when ffmpeg is not on PATH, and only asserts the detection matches reality when it is.
    /// </summary>
    [Fact]
    public void IsAvailable_AgreesWithAnIndependentProbeForFfmpegOnPath()
    {
        bool ffmpegOnPath = ProbeForFfmpeg();

        if (!ffmpegOnPath)
        {
            // No Skip.If in xUnit v3's Assert - a plain early return is the idiomatic way to
            // make this a no-op on a machine without ffmpeg rather than a false failure.
            return;
        }

        Assert.True(Encoder.IsAvailable(), "ffmpeg is on PATH by an independent probe, but Encoder.IsAvailable() said no.");
    }

    /// <summary>
    /// A probe genuinely independent of Encoder's own implementation: it never launches a
    /// process at all, so it cannot share Encoder.IsAvailable()'s process-launch machinery (or
    /// any bug in it) and pass anyway. Instead it does exactly what the OS's own executable
    /// search does when a caller runs a bare "ffmpeg" - walk PATH, and on Windows, PATHEXT's
    /// extensions - and checks whether any candidate file exists.
    /// </summary>
    /// <remarks>
    /// The previous version of this probe was a verbatim copy of Encoder.IsAvailable()'s
    /// Process.Start/-version implementation, which defeated the entire point: a shared defect
    /// (wrong executable name, wrong flag, an over-broad catch) would manifest identically on
    /// both sides, and this test would still pass - even hide a real detection bug - rather than
    /// fail. A probe that never calls Process.Start cannot share that failure mode.
    /// </remarks>
    private static bool ProbeForFfmpeg()
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return false;
        }

        string[] candidateNames = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(extension => "ffmpeg" + extension)
                .ToArray()
            : ["ffmpeg"];

        return pathVariable
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => candidateNames.Select(name => Path.Combine(directory, name)))
            .Any(File.Exists);
    }
}
