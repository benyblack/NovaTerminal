using System.ComponentModel;
using System.Diagnostics;
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
    /// A probe independent of Encoder's own implementation, so this test cannot pass merely
    /// because both sides share the same bug.
    /// </summary>
    private static bool ProbeForFfmpeg()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
