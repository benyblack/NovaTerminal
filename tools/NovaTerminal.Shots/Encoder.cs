using System.Diagnostics;

namespace NovaTerminal.Shots;

public static class Encoder
{
    public static bool IsAvailable()
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public static void ToWebm(string frameDirectory, string outputPath, int fps) =>
        Run($"-y -framerate {fps} -i \"{Path.Combine(frameDirectory, "frame-%05d.png")}\" " +
            $"-c:v libvpx-vp9 -pix_fmt yuv420p -b:v 0 -crf 32 \"{outputPath}\"");

    /// <summary>
    /// Two passes: palettegen then paletteuse. A GIF encoded without a generated palette
    /// banding-crushes terminal text, which is the one thing these clips exist to show.
    /// </summary>
    public static void ToGif(string frameDirectory, string outputPath, int fps)
    {
        string pattern = Path.Combine(frameDirectory, "frame-%05d.png");
        string palette = Path.Combine(frameDirectory, "palette.png");

        Run($"-y -framerate {fps} -i \"{pattern}\" -vf palettegen=stats_mode=diff \"{palette}\"");
        Run($"-y -framerate {fps} -i \"{pattern}\" -i \"{palette}\" " +
            $"-lavfi \"paletteuse=dither=bayer:bayer_scale=3\" \"{outputPath}\"");
    }

    private static void Run(string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo("ffmpeg", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start ffmpeg.");

        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {stderr}");
        }
    }
}
