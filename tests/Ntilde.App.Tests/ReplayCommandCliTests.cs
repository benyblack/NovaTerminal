using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NovaTerminal.Replay;
using NovaTerminal.VT;

namespace NovaTerminal.Tests;

/// <summary>
/// Tests for <c>NovaTerminal.Cli --replay</c> (A4 slice 3,
/// docs/plans/2026-07-07-agent-host-a4-replay-design.md): headless Virtual-mode
/// replay of a .rec file printing the deterministic <see cref="BufferSnapshot"/>
/// formatted screen. Exit codes: 0 success, 1 unreadable/truncated (partial
/// output still printed), 2 usage.
/// </summary>
public sealed class ReplayCommandCliTests : IDisposable
{
    private readonly string _tempDir;

    public ReplayCommandCliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nova-replaycli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string NewRecPath() => Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8] + ".rec");

    private static string WriteRecording(string path, Action<PtyRecorder> record, int cols = 40, int rows = 10)
    {
        using (var recorder = new PtyRecorder(path, cols, rows, "test-shell"))
        {
            record(recorder);
        }
        return path;
    }

    // ── Flag detection & usage errors ────────────────────────────────────────

    [Fact]
    public void IsSupportedCliMode_DetectsTheReplayFlag()
    {
        Assert.False(ReplayCommand.IsSupportedCliMode(["--help"]));
        Assert.True(ReplayCommand.IsSupportedCliMode(["--replay", "x.rec"]));
    }

    [Fact]
    public void Execute_UsageErrors_Exit2_WithUsageOnStderr()
    {
        string[][] badInvocations =
        {
            new[] { "--replay" },                                   // missing path
            new[] { "--replay", "--attributes" },                   // flag where path expected
            new[] { "--replay", "a.rec", "--bogus" },               // unknown argument
            new[] { "--replay", "a.rec", "--replay", "b.rec" },     // duplicate --replay
            new[] { "--replay", "a.rec", "--attributes", "--attributes" }, // duplicate --attributes
        };

        foreach (string[] args in badInvocations)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = ReplayCommand.Execute(args, stdout, stderr);

            Assert.Equal(2, exitCode);
            Assert.Contains("Usage: NovaTerminal.Cli --replay", stderr.ToString());
        }
    }

    [Fact]
    public void Execute_MissingFile_Exit1()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = ReplayCommand.Execute(["--replay", Path.Combine(_tempDir, "nope.rec")], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", stderr.ToString());
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_RendersTheFinalScreen_MatchingADirectlyFedBuffer()
    {
        // The headless CLI is the same deterministic pipeline the parity tests
        // use: replaying the file must print exactly the snapshot of a buffer
        // fed the same bytes directly (incl. resize and split-emoji chunks).
        byte[] rocket = Encoding.UTF8.GetBytes("🚀");
        string recPath = WriteRecording(NewRecPath(), recorder =>
        {
            byte[] first = Encoding.UTF8.GetBytes("alpha\r\n\x1b[1mbold\x1b[0m ");
            recorder.RecordChunk(first, first.Length);
            recorder.RecordResize(30, 6);
            recorder.RecordChunkAt(10, rocket.AsSpan(0, 2).ToArray(), 2);
            recorder.RecordChunkAt(20, rocket.AsSpan(2, 2).ToArray(), 2);
        });

        var expectedBuffer = new TerminalBuffer(80, 24);
        var expectedParser = new AnsiParser(expectedBuffer);
        expectedBuffer.Resize(40, 10); // v2 header geometry, applied before data
        expectedParser.Process("alpha\r\n\x1b[1mbold\x1b[0m ");
        expectedBuffer.Resize(30, 6);
        expectedParser.Process("🚀");
        string expected = BufferSnapshot.Capture(expectedBuffer, includeAttributes: false).ToFormattedString();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = ReplayCommand.Execute(["--replay", recPath], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Equal(expected.TrimEnd('\r', '\n'), stdout.ToString().TrimEnd('\r', '\n'));
        Assert.Contains("🚀", stdout.ToString());
    }

    [Fact]
    public void Execute_WithAttributes_PrintsAttributeLines()
    {
        string recPath = WriteRecording(NewRecPath(), recorder =>
        {
            byte[] data = Encoding.UTF8.GetBytes("\x1b[31mred\x1b[0m plain");
            recorder.RecordChunk(data, data.Length);
        });

        using var plainOut = new StringWriter();
        using var attrOut = new StringWriter();
        using var stderr = new StringWriter();

        Assert.Equal(0, ReplayCommand.Execute(["--replay", recPath], plainOut, stderr));
        Assert.Equal(0, ReplayCommand.Execute(["--replay", recPath, "--attributes"], attrOut, stderr));

        var buffer = new TerminalBuffer(40, 10);
        new AnsiParser(buffer).Process("\x1b[31mred\x1b[0m plain");
        string expectedAttrs = BufferSnapshot.Capture(buffer, includeAttributes: true).ToFormattedString();

        Assert.Equal(expectedAttrs.TrimEnd('\r', '\n'), attrOut.ToString().TrimEnd('\r', '\n'));
        Assert.NotEqual(plainOut.ToString(), attrOut.ToString());
    }

    // ── Truncated / unreadable files ─────────────────────────────────────────

    [Fact]
    public async Task Execute_TruncatedTail_Exit1_WithPartialScreenAndWarning()
    {
        // Flight-recorder exports of live sessions can end mid-write; the CLI
        // must print the readable prefix and warn instead of failing outright.
        string recPath = WriteRecording(NewRecPath(), recorder =>
        {
            byte[] data = Encoding.UTF8.GetBytes("visible content");
            recorder.RecordChunk(data, data.Length);
        });
        string full = await File.ReadAllTextAsync(recPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            recPath,
            full.TrimEnd('\r', '\n') + "\n{\"t\":99,\"type\":\"data\",\"d\":\"QUJD",
            TestContext.Current.CancellationToken); // torn final line

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = ReplayCommand.Execute(["--replay", recPath], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("visible content", stdout.ToString());
        Assert.Contains("truncated", stderr.ToString());
    }

    [Fact]
    public void Execute_UnreadableFile_Exit1_WithError()
    {
        string recPath = Path.Combine(_tempDir, "garbage.rec");
        File.WriteAllText(recPath, "this is not a replay file\nnot json either\n");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = ReplayCommand.Execute(["--replay", recPath], stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Failed to replay", stderr.ToString());
    }

    // ── End-to-end with the flight recorder (the A4 loop) ────────────────────

    [Fact]
    public void Execute_RendersAFlightRecorderExport()
    {
        // The full A4 story: session bytes → flight ring → exportReplay file →
        // headless CLI → deterministic screen.
        var ring = new FlightRecordingBuffer(1024 * 1024, 40, 10, clock: static () => 0);
        byte[] bytes = Encoding.UTF8.GetBytes("agent did this\r\nline two");
        ring.RecordChunk(bytes, bytes.Length);
        string recPath = NewRecPath();
        ring.ExportTo(recPath, "stub-shell");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = ReplayCommand.Execute(["--replay", recPath], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("agent did this", stdout.ToString());
        Assert.Contains("line two", stdout.ToString());
    }
}
