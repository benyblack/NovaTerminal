using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Core.Replay
{
    public class PtyRecorder : IDisposable
    {
        private readonly string _filePath;
        private readonly BlockingCollection<ReplayChunk> _queue = new BlockingCollection<ReplayChunk>();
        private readonly Task _writeTask;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly DateTime _startTime;

        public PtyRecorder(string filePath)
        {
            _filePath = filePath;
            _startTime = DateTime.UtcNow;
            _writeTask = Task.Run(WriteLoop);
        }

        public void RecordChunk(byte[] data, int length)
        {
            if (_cts.IsCancellationRequested) return;

            // Copy buffer to avoid modification before write
            byte[] copy = new byte[length];
            Array.Copy(data, copy, length);

            _queue.Add(new ReplayChunk
            {
                TimeOffsetMs = (long)(DateTime.UtcNow - _startTime).TotalMilliseconds,
                Data = Convert.ToBase64String(copy)
            });
        }

        private async Task WriteLoop()
        {
            try
            {
                using var writer = new StreamWriter(_filePath, append: false);
                foreach (var chunk in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    // Manual JSON-like serialization to avoid AOT reflection issues
                    // Format: {"t":TIK,"d":"DATA"}
                    string json = $"{{\"t\":{chunk.TimeOffsetMs},\"d\":\"{chunk.Data}\"}}";
                    await writer.WriteLineAsync(json);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[PtyRecorder] Write failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            // Wait for queue to drain gracefully
            try
            {
                if (!_writeTask.Wait(2000))
                {
                    _cts.Cancel(); // Force cancel if stuck
                }
            }
            catch { }

            _cts.Dispose();
        }
    }

    public class ReplayChunk
    {
        public long TimeOffsetMs { get; set; }
        public string Data { get; set; } = "";
    }
}
