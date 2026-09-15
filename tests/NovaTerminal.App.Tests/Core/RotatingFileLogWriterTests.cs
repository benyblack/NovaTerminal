using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// The debug log's sink. Covers what the previous <c>File.AppendAllText</c>-per-call version got
/// wrong: it wrote synchronously on the producer's thread, and it grew without limit for as long
/// as the process lived, so a long-lived SSH tab got slower and the file reached gigabytes.
/// </summary>
public class RotatingFileLogWriterTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "nova-logwriter-tests", Guid.NewGuid().ToString("N"), "debug.log");

    private static void Cleanup(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetDirectoryName(path));
            if (dir != null && Directory.Exists(dir)) { /* leaf only */ }
            string? leaf = Path.GetDirectoryName(path);
            if (leaf != null && Directory.Exists(leaf)) Directory.Delete(leaf, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    /// <summary>Reads the file while the writer still holds it open (FileShare.ReadWrite).</summary>
    private static string ReadAll(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void QueuedMessages_ReachTheFile()
    {
        string path = TempPath();
        try
        {
            using (var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 1024))
            {
                writer.Write("first");
                writer.Write("second");
            } // Dispose drains and flushes.

            string contents = ReadAll(path);
            Assert.Contains("first", contents);
            Assert.Contains("second", contents);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Dispose_FlushesWhatIsStillBuffered()
    {
        // The whole point of the background thread is that Write returns before the disk does.
        // If Dispose did not drain, the last lines before a crash — the ones worth having — would
        // be the ones lost.
        string path = TempPath();
        try
        {
            using (var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 1024))
            {
                for (int i = 0; i < 500; i++) writer.Write($"line {i}");
            }

            string contents = ReadAll(path);
            Assert.Contains("line 0", contents);
            Assert.Contains("line 499", contents);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TheFileIsBounded_AndOnePreviousGenerationIsKept()
    {
        string path = TempPath();
        try
        {
            const long maxBytes = 4096;
            using (var writer = new RotatingFileLogWriter(path, maxBytes, queueCapacity: 4096))
            {
                // Comfortably more than one generation's worth.
                for (int i = 0; i < 2000; i++) writer.Write(new string('x', 100));
            }

            long live = new FileInfo(path).Length;

            // The cap is enforced per generation, not across the run: this is what stops a
            // 24-hour session from writing gigabytes.
            Assert.True(live < maxBytes * 2, $"live generation was {live} bytes, cap {maxBytes}");
            Assert.True(File.Exists(path + ".1"), "the previous generation should be retained");
        }
        finally
        {
            Cleanup(path);
            try { File.Delete(path + ".1"); } catch { }
        }
    }

    [Fact]
    public void RotationIsRecordedInTheLog()
    {
        string path = TempPath();
        try
        {
            using (var writer = new RotatingFileLogWriter(path, maxBytes: 2048, queueCapacity: 4096))
            {
                for (int i = 0; i < 500; i++) writer.Write(new string('y', 100));
            }

            // A log that silently discards its own history is a log that cannot be reasoned about.
            Assert.Contains("rotated", ReadAll(path));
        }
        finally
        {
            Cleanup(path);
            try { File.Delete(path + ".1"); } catch { }
        }
    }

    [Fact]
    public void AFullQueue_DropsRatherThanBlocking()
    {
        string path = TempPath();
        try
        {
            // Capacity 1 with a producer far faster than the disk: drops are expected, a stall is
            // not. Write is reached from the PTY read thread, where blocking is the original bug.
            using var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 1);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 20000; i++) writer.Write($"flood {i}");
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                $"20k writes took {sw.Elapsed.TotalSeconds:F1}s — Write is blocking on the disk");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ConcurrentProducers_AreSafe()
    {
        // The real callers are the PTY read thread, the UI thread and the render thread at once.
        string path = TempPath();
        try
        {
            using (var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 8192))
            {
                var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
                {
                    for (int i = 0; i < 200; i++) writer.Write($"t{t} line {i}");
                })).ToArray();

                foreach (var t in threads) t.Start();
                foreach (var t in threads) t.Join();
            }

            string contents = ReadAll(path);
            // Not a count assertion: capacity 8192 is above 1600 so nothing should drop, but the
            // property under test is that concurrent writers neither throw nor corrupt a line.
            Assert.Contains("t0 line 0", contents);
            Assert.Contains("t7 line 199", contents);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AnUnwritablePath_DegradesToANoOp()
    {
        // A diagnostic must never be able to fail the app. The directory is created for us, so an
        // unusable path is the interesting case: a file where a directory has to go.
        string blocker = Path.Combine(Path.GetTempPath(), "nova-logwriter-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        try
        {
            string path = Path.Combine(blocker, "debug.log");
            using var writer = new RotatingFileLogWriter(path, maxBytes: 1024, queueCapacity: 16);
            writer.Write("this goes nowhere, quietly");
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        string path = TempPath();
        try
        {
            var writer = new RotatingFileLogWriter(path, maxBytes: 1 << 20, queueCapacity: 16);
            writer.Write("once");
            writer.Dispose();
            writer.Dispose();
            writer.Write("after dispose, swallowed");
        }
        finally { Cleanup(path); }
    }
}
