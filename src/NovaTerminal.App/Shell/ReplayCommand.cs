using System;
using System.IO;
using System.Text;
using NovaTerminal.Replay;
using NovaTerminal.VT;

namespace NovaTerminal;

/// <summary>
/// Headless replay for CI and agent-run postmortems (milestone A4,
/// docs/plans/2026-07-07-agent-host-a4-replay-design.md):
/// <c>NovaTerminal.Cli --replay &lt;file&gt; [--attributes]</c> runs a replay
/// file (v2, with v1 compatibility) through the deterministic core in Virtual
/// mode and prints the final screen as <see cref="BufferSnapshot"/> formatted
/// text — the same snapshot the golden/parity tests consume.
///
/// Exit codes: 0 = success; 1 = unreadable or truncated file (any partial
/// screen is still printed, with a warning on stderr); 2 = usage error.
/// </summary>
internal static class ReplayCommand
{
    private const string ReplayFlag = "--replay";
    private const string AttributesFlag = "--attributes";

    public static bool IsSupportedCliMode(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Array.Exists(args, arg => string.Equals(arg, ReplayFlag, StringComparison.Ordinal));
    }

    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (!TryParseArguments(args, stderr, out string filePath, out bool includeAttributes))
        {
            return 2;
        }

        if (!File.Exists(filePath))
        {
            stderr.WriteLine($"Replay file not found: '{filePath}'.");
            return 1;
        }

        // Header geometry (v2) arrives via the resize callback before any data;
        // v1 files have no header, so 80x24 is the fallback start geometry.
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var decoder = Encoding.UTF8.GetDecoder();

        ReplayRunResult result;
        try
        {
            var runner = new ReplayRunner(filePath);
            // CLI constraint (established by the other commands): no async Main,
            // bridge with GetAwaiter().GetResult().
            result = runner.RunWithResultAsync(
                onDataCallback: data =>
                {
                    // Pooled decode buffer: one rent/return per chunk instead of a
                    // fresh array — large replays would otherwise churn the GC.
                    int maxCharCount = Encoding.UTF8.GetMaxCharCount(data.Length);
                    char[] chars = System.Buffers.ArrayPool<char>.Shared.Rent(maxCharCount);
                    try
                    {
                        int charCount = decoder.GetChars(data, 0, data.Length, chars, 0);
                        if (charCount > 0)
                        {
                            parser.Process(new string(chars, 0, charCount));
                        }
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<char>.Shared.Return(chars);
                    }
                    return System.Threading.Tasks.Task.CompletedTask;
                },
                onResizeCallback: (cols, rows) =>
                {
                    buffer.Resize(cols, rows);
                    return System.Threading.Tasks.Task.CompletedTask;
                },
                onSnapshotCallback: snapshot =>
                {
                    buffer.ApplySnapshot(snapshot);
                    return System.Threading.Tasks.Task.CompletedTask;
                },
                options: new ReplayRunOptions { PlaybackMode = ReplayPlaybackMode.Virtual })
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Unreadable from the first line (bad header/JSON) or a mid-file
            // failure the runner does not classify as tail truncation.
            stderr.WriteLine($"Failed to replay '{filePath}': {ex.Message}");
            return 1;
        }

        BufferSnapshot snapshotOut = BufferSnapshot.Capture(buffer, includeAttributes);
        stdout.WriteLine(snapshotOut.ToFormattedString());

        if (result.Truncated)
        {
            // Partial output was still printed above — flight-recorder exports of
            // live sessions can legitimately end mid-write.
            stderr.WriteLine($"Warning: replay file is truncated after {result.EventsProcessed} event(s); the screen above reflects the readable prefix.");
            return 1;
        }

        return 0;
    }

    private static bool TryParseArguments(string[] args, TextWriter stderr, out string filePath, out bool includeAttributes)
    {
        filePath = string.Empty;
        includeAttributes = false;
        bool seenReplayFlag = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case ReplayFlag when seenReplayFlag:
                    stderr.WriteLine("Duplicate argument '--replay'.");
                    PrintUsage(stderr);
                    return false;
                case ReplayFlag:
                    seenReplayFlag = true;
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        stderr.WriteLine("'--replay' requires a file path.");
                        PrintUsage(stderr);
                        return false;
                    }
                    filePath = args[++i];
                    break;
                case AttributesFlag when includeAttributes:
                    stderr.WriteLine("Duplicate argument '--attributes'.");
                    PrintUsage(stderr);
                    return false;
                case AttributesFlag:
                    includeAttributes = true;
                    break;
                default:
                    stderr.WriteLine($"Unknown argument '{arg}' for replay mode.");
                    PrintUsage(stderr);
                    return false;
            }
        }

        if (!seenReplayFlag || string.IsNullOrWhiteSpace(filePath))
        {
            PrintUsage(stderr);
            return false;
        }

        return true;
    }

    private static void PrintUsage(TextWriter stderr)
    {
        stderr.WriteLine("Usage: NovaTerminal.Cli --replay <file> [--attributes]");
        stderr.WriteLine("Replays a NovaTerminal .rec file headlessly and prints the final screen.");
    }
}
