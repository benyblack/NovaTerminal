using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NovaTerminal.Shots;

public static class Encoder
{
    /// <summary>How long a version probe may take before it is treated as hung.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a real encode may take before it is treated as hung.</summary>
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(5);

    public static bool IsAvailable()
    {
        try
        {
            return Run("-version", ProbeTimeout, out _);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public static void ToWebm(string frameDirectory, string outputPath, int fps) =>
        EncodeToTemp(outputPath, temp => RunOrThrow(
            $"-y -framerate {fps} -i \"{Path.Combine(frameDirectory, "frame-%05d.png")}\" " +
            $"-c:v libvpx-vp9 -pix_fmt yuv420p -b:v 0 -crf 32 \"{temp}\""));

    /// <summary>
    /// Two passes: palettegen then paletteuse. A GIF encoded without a generated palette
    /// banding-crushes terminal text, which is the one thing these clips exist to show.
    /// </summary>
    public static void ToGif(string frameDirectory, string outputPath, int fps) =>
        EncodeToTemp(outputPath, temp =>
        {
            string pattern = Path.Combine(frameDirectory, "frame-%05d.png");
            string palette = Path.Combine(frameDirectory, "palette.png");

            RunOrThrow($"-y -framerate {fps} -i \"{pattern}\" -vf palettegen=stats_mode=diff \"{palette}\"");
            RunOrThrow($"-y -framerate {fps} -i \"{pattern}\" -i \"{palette}\" " +
                $"-lavfi \"paletteuse=dither=bayer:bayer_scale=3\" \"{temp}\"");
        });

    /// <summary>
    /// Encodes into a scratch file beside <paramref name="outputPath"/> and moves it into place
    /// only once <paramref name="encode"/> returns successfully.
    /// </summary>
    /// <remarks>
    /// Without this, <c>Run</c>'s <c>-y</c> writes straight to <paramref name="outputPath"/>, so a
    /// failure partway through a multi-pass encode (ToGif's second pass, say) leaves a truncated
    /// file at the real path - and if a previous successful run already produced a good file
    /// there, a failed re-run's crash mid-write corrupts it in place. Worse, if the failure
    /// happens before <c>ffmpeg</c> ever touches <paramref name="outputPath"/> at all, the stale
    /// file from that previous successful run is left behind looking exactly like a fresh one to
    /// anything that only checks whether the path exists rather than the harness's exit code. So a
    /// failure here deletes whatever is at <paramref name="outputPath"/> too - an absent file is a
    /// clear signal; a stale one is not.
    /// </remarks>
    private static void EncodeToTemp(string outputPath, Action<string> encode)
    {
        // The real extension must survive onto the temp path, not just get a ".tmp" tacked on
        // the end: ffmpeg's muxer is chosen from the output filename's extension, so
        // "clip-agent.webm.tmp" fails to start at all ("Unable to choose an output format") -
        // confirmed by reproducing the failure directly. "clip-agent.tmp.webm" keeps the real
        // extension last, so format detection still works.
        string tempPath = Path.Combine(
            Path.GetDirectoryName(outputPath) is { Length: > 0 } directory ? directory : ".",
            Path.GetFileNameWithoutExtension(outputPath) + ".tmp" + Path.GetExtension(outputPath));

        try
        {
            encode(tempPath);
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            TryDelete(outputPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort - if something else still has it open, there is nothing more
            // constructive to do from here.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    private static void RunOrThrow(string arguments)
    {
        if (!Run(arguments, EncodeTimeout, out string stderr))
        {
            throw new InvalidOperationException($"ffmpeg failed: {stderr}");
        }
    }

    /// <summary>
    /// Runs ffmpeg and waits for it to exit, draining both redirected streams asynchronously
    /// first.
    /// </summary>
    /// <remarks>
    /// Both streams share a fixed-size OS pipe buffer. Reading only one of them (or neither)
    /// before a blocking <c>WaitForExit()</c> is only safe for as long as the child never writes
    /// enough combined output to fill the other; the moment it does, the child blocks on write()
    /// and <c>WaitForExit()</c> never returns. Measured here: <c>ffmpeg -version</c> writes ~1.5KB
    /// to stdout and nothing to stderr, while a real encode writes nothing to stdout and a few KB
    /// of progress to stderr - so either stream can be the one that matters depending on the
    /// invocation, which is exactly why both must be drained rather than just the one that
    /// happened to matter for today's call sites. This is the same deadlock shape DemoWorld.Git
    /// avoids and CLAUDE.md's build-wrapper rule exists for. <see cref="Process.WaitForExit(int)"/>
    /// additionally bounds the wait, so a future ffmpeg build or flag change that hangs still
    /// returns control here instead of hanging the whole harness.
    /// </remarks>
    private static bool Run(string arguments, TimeSpan timeout, out string stderr)
    {
        var stderrBuilder = new StringBuilder();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("ffmpeg", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start ffmpeg.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already exited between the timeout and the kill attempt, or could not be
                // signalled - either way, there is nothing more to do but report the timeout.
            }

            stderr = stderrBuilder.ToString();
            throw new InvalidOperationException(
                $"ffmpeg did not exit within {timeout.TotalSeconds:0}s and was killed. " +
                $"Arguments: {arguments}. Captured stderr: {stderr}");
        }

        // The timed WaitForExit(int) overload only guarantees the process itself has exited, not
        // that BeginErrorReadLine's asynchronous callbacks have finished delivering every buffered
        // line - .NET's own documented gotcha for event-based redirected reads. Without this
        // second, parameterless WaitForExit() (which does wait for the redirected streams to
        // close), stderrBuilder can still be mid-flight when read below, truncating exactly the
        // diagnostic text a failure needs. Bounded only by the caller's own timeout having already
        // passed, so this cannot itself hang.
        process.WaitForExit();

        stderr = stderrBuilder.ToString();
        return process.ExitCode == 0;
    }
}
