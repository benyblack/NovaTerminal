using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Ntilde.Shell
{
    /// <summary>
    /// A bounded queue drained by one background thread into a buffered, size-capped file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every property here replaces one that caused a real bug. <see cref="AppLogger"/> used to be
    /// <c>File.AppendAllText</c> per call — open, append, flush, close — under a process-global
    /// lock, with no size limit. That put a synchronous disk write on whatever thread produced the
    /// message, which for the parser diagnostics is the PTY read thread, and serialized it against
    /// the UI and render threads through the shared lock. Measured on a remote ncurses redraw:
    /// 0.23 ms/frame of parsing became 1.24 ms/frame, and the file grew at 1-10 GB/day. A
    /// long-lived SSH tab got slower the longer it stayed open, because the file was only
    /// truncated at startup.
    /// </para>
    /// <para>
    /// So: producers hand off and return (<see cref="Write"/> is a queue push), one writer owns the
    /// file, the queue is bounded so a log storm costs dropped lines rather than memory, and the
    /// file rotates so a long session costs bounded disk. Drops and rotations are both recorded in
    /// the log itself — a sink that quietly loses messages is worse than a slow one.
    /// </para>
    /// <para>
    /// Separate from <see cref="AppLogger"/>, which is a process-global static bound to a path
    /// resolved at type-initialization time, so none of the above could be asserted. This class
    /// takes its path and its limits, which is what makes the rotation and the drop accounting
    /// testable at all.
    /// </para>
    /// </remarks>
    internal sealed class RotatingFileLogWriter : IDisposable
    {
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly BlockingCollection<string> _queue;
        private readonly Thread _pump;

        private int _droppedSinceLastReport;
        private StreamWriter? _writer;
        private long _bytesWritten;

        /// <param name="path">The live log file. One previous generation is kept at <c>path + ".1"</c>.</param>
        /// <param name="maxBytes">Rotate once the live file passes this.</param>
        /// <param name="queueCapacity">
        /// Depth of the hand-off queue. Deep enough that an ordinary burst is never dropped,
        /// shallow enough that a runaway producer cannot grow the heap.
        /// </param>
        public RotatingFileLogWriter(string path, long maxBytes, int queueCapacity)
        {
            _path = path;
            _maxBytes = maxBytes;
            _queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), queueCapacity);

            // Guarded like every other filesystem call here, and for the sharper reason: this runs
            // inside AppLogger's static initializer, so an exception escaping it becomes a
            // TypeInitializationException on the first log call anywhere in the app. A diagnostic
            // that cannot write must degrade to silence, never take the process with it.
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch
            {
                // TryOpen will fail too, and leave _writer null. That is the no-op state.
            }

            // Start each run on a fresh file, as the original did.
            TryOpen();

            _pump = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "Ntilde.LogWriter",
            };
            _pump.Start();
        }

        /// <summary>Messages dropped because the queue was full. Test seam and health signal.</summary>
        public int DroppedCount => Volatile.Read(ref _droppedSinceLastReport);

        /// <summary>
        /// Queues one line. Never blocks, never throws, and never touches the disk on the caller's
        /// thread — this is reached from the PTY read thread and the render thread.
        /// </summary>
        public void Write(string message)
        {
            try
            {
                string entry = string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");

                if (!_queue.TryAdd(entry))
                {
                    // Full: the writer is behind, or the disk is. Dropping the newest line is the
                    // only option that keeps this method non-blocking; the count is reported by
                    // the writer as soon as it catches up, so the gap is visible in the log.
                    Interlocked.Increment(ref _droppedSinceLastReport);
                }
            }
            catch
            {
                // Logging must never break the application — including after CompleteAdding.
            }
        }

        /// <summary>Stops accepting messages, then drains and flushes what is already queued.</summary>
        /// <remarks>
        /// A background thread is killed mid-buffer at shutdown, which would lose exactly the lines
        /// a crash report needs. Safe to call more than once.
        /// </remarks>
        public void Dispose()
        {
            try
            {
                if (!_queue.IsAddingCompleted)
                {
                    _queue.CompleteAdding();
                }
            }
            catch
            {
                // Racing another Dispose: the winner drains.
            }

            try
            {
                _pump.Join(TimeSpan.FromSeconds(2));
            }
            catch (ThreadStateException)
            {
                // The pump never started (the constructor's Thread.Start threw). There is nothing
                // to drain, and Dispose must stay safe to call on a half-built writer.
            }
            catch (ThreadInterruptedException)
            {
                // Something interrupted the disposing thread while it waited. The pump is a
                // background thread and the queue is already closed, so stopping the wait here
                // costs at most the tail of the buffer — never a hang on the way out.
            }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (string entry in _queue.GetConsumingEnumerable())
                {
                    WriteLine(entry);

                    // Drain whatever else is already queued before flushing, so a burst costs one
                    // flush rather than one per line.
                    while (_queue.TryTake(out string? more))
                    {
                        WriteLine(more);
                    }

                    int dropped = Interlocked.Exchange(ref _droppedSinceLastReport, 0);
                    if (dropped > 0)
                    {
                        WriteLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Warning] log writer dropped {dropped} message(s): queue full."));
                    }

                    try { _writer?.Flush(); } catch { /* disk gone; keep draining */ }
                }
            }
            catch (ObjectDisposedException)
            {
                // The queue was disposed out from under the consumer. Only reachable at
                // shutdown, and the finally below still flushes what the stream is holding.
            }
            catch (InvalidOperationException)
            {
                // GetConsumingEnumerable racing CompleteAdding. Same shutdown path, same
                // remedy: fall through and flush.
            }
            finally
            {
                try
                {
                    _writer?.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
                {
                    // Nothing actionable on the way down: the destination may already be gone
                    // (removed file, full disk, unmounted volume). Throwing here would replace a
                    // lost diagnostic with an unhandled exception on a background thread.
                }

                try
                {
                    _writer?.Dispose();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Same: the handle is being abandoned regardless, and the process is exiting.
                }

                _writer = null;
            }
        }

        private void WriteLine(string entry)
        {
            if (_writer == null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(entry);

                // Counted rather than measured: asking the stream for its length on every line
                // would defeat the buffering this class exists for.
                _bytesWritten += entry.Length + Environment.NewLine.Length;

                if (_bytesWritten >= _maxBytes)
                {
                    Rotate();
                }
            }
            catch
            {
                // Disk full, file removed, permissions changed mid-run: stop writing rather than
                // throwing on the writer thread, and let the next rotation try to recover.
            }
        }

        private void Rotate()
        {
            string previous = _path + ".1";
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;

                File.Delete(previous);
                File.Move(_path, previous);
            }
            catch
            {
                // If the move fails the reopen below truncates instead, which still bounds growth.
            }

            TryOpen();

            // Written straight to the stream rather than through WriteLine, so the size check
            // there cannot re-enter this method.
            try
            {
                _writer?.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === rotated; previous generation at {Path.GetFileName(previous)} ==="));
            }
            catch
            {
                // Same degradation as every other write here.
            }
        }

        private void TryOpen()
        {
            try
            {
                var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024)
                {
                    AutoFlush = false,
                };
            }
            catch
            {
                // No log file this run. Everything above degrades to a no-op rather than failing
                // the app over a diagnostic.
                _writer = null;
            }

            _bytesWritten = 0;
        }
    }
}
