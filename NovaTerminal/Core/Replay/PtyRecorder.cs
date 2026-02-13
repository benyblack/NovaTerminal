using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Core.Replay
{
    public class PtyRecorder : IDisposable
    {
        private readonly string _filePath;
        private readonly BlockingCollection<ReplayEvent> _queue = new BlockingCollection<ReplayEvent>();
        private readonly Task _writeTask;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly DateTime _startTime;
        private readonly ReplayHeader _header;

        public PtyRecorder(string filePath, int cols, int rows, string shell = "")
        {
            _filePath = filePath;
            _startTime = DateTime.UtcNow;

            _header = new ReplayHeader
            {
                Type = "novarec",
                Cols = cols,
                Rows = rows,
                Shell = shell
            };

            _writeTask = Task.Run(WriteLoop);
        }

        public void RecordChunk(byte[] data, int length)
        {
            if (_cts.IsCancellationRequested) return;

            byte[] copy = new byte[length];
            Array.Copy(data, copy, length);

            _queue.Add(new ReplayEvent
            {
                TimeOffsetMs = GetTimestamp(),
                Type = "data",
                Data = Convert.ToBase64String(copy)
            });
        }

        public void RecordResize(int cols, int rows)
        {
            if (_cts.IsCancellationRequested) return;

            _queue.Add(new ReplayEvent
            {
                TimeOffsetMs = GetTimestamp(),
                Type = "resize",
                Cols = cols,
                Rows = rows
            });
        }

        public void RecordMarker(string name)
        {
            if (_cts.IsCancellationRequested) return;

            _queue.Add(new ReplayEvent
            {
                TimeOffsetMs = GetTimestamp(),
                Type = "marker",
                MarkerName = name
            });
        }

        private long GetTimestamp() => (long)(DateTime.UtcNow - _startTime).TotalMilliseconds;

        private async Task WriteLoop()
        {
            try
            {
                using var writer = new StreamWriter(_filePath, append: false);

                // Write v2 Header
                string headerJson = JsonSerializer.Serialize(_header, ReplayJsonContext.Default.ReplayHeader);
                await writer.WriteLineAsync(headerJson);

                foreach (var ev in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    string json = JsonSerializer.Serialize(ev, ReplayJsonContext.Default.ReplayEvent);
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
            try
            {
                if (!_writeTask.Wait(2000))
                {
                    _cts.Cancel();
                }
            }
            catch { }

            _cts.Dispose();
        }
    }
}
