using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace NovaTerminal.Core.Replay
{
    public class ReplayRunner
    {
        private readonly string _filePath;

        public ReplayRunner(string filePath)
        {
            _filePath = filePath;
        }

        public async Task RunAsync(
            Func<byte[], Task> onDataCallback,
            Func<int, int, Task>? onResizeCallback = null,
            Func<string, Task>? onMarkerCallback = null,
            Func<string, Task>? onInputCallback = null,
            Func<ReplaySnapshot, Task>? onSnapshotCallback = null,
            bool realtime = false)
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException("Replay file not found", _filePath);

            using var reader = new StreamReader(_filePath);
            string? line = await reader.ReadLineAsync();
            if (line == null) return;

            // Detect Version
            bool isV2 = false;
            try
            {
                // Try to parse the VERY FIRST LINE as a v2 header
                var header = JsonSerializer.Deserialize(line, ReplayJsonContext.Default.ReplayHeader);
                if (header != null && header.Type == "novarec" && header.Version == 2)
                {
                    isV2 = true;
                    if (onResizeCallback != null)
                    {
                        await onResizeCallback(header.Cols, header.Rows);
                    }
                }
            }
            catch { }

            long lastOffset = 0;
            // If it's V2, we skip the first line (header).
            // If it's V1, we process the first line immediately.
            bool skipCurrentLine = isV2;

            while (line != null)
            {
                if (!skipCurrentLine)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (isV2)
                        {
                            lastOffset = await ProcessV2Line(line, onDataCallback, onResizeCallback, onMarkerCallback, onInputCallback, onSnapshotCallback, realtime, lastOffset);
                        }
                        else
                        {
                            lastOffset = await ProcessV1Line(line, onDataCallback, realtime, lastOffset);
                        }
                    }
                }
                skipCurrentLine = false;
                line = await reader.ReadLineAsync();
            }
        }

        private async Task<long> ProcessV2Line(
            string line,
            Func<byte[], Task> onDataCallback,
            Func<int, int, Task>? onResizeCallback,
            Func<string, Task>? onMarkerCallback,
            Func<string, Task>? onInputCallback,
            Func<ReplaySnapshot, Task>? onSnapshotCallback,
            bool realtime,
            long lastOffset)
        {
            try
            {
                var ev = JsonSerializer.Deserialize(line, ReplayJsonContext.Default.ReplayEvent);
                if (ev == null) return lastOffset;

                if (realtime)
                {
                    long delay = ev.TimeOffsetMs - lastOffset;
                    if (delay > 0) await Task.Delay((int)delay);
                    lastOffset = ev.TimeOffsetMs;
                }

                switch (ev.Type)
                {
                    case "data":
                        if (!string.IsNullOrEmpty(ev.Data))
                        {
                            byte[] data = Convert.FromBase64String(ev.Data);
                            await onDataCallback(data);
                        }
                        break;
                    case "resize":
                        if (ev.Cols.HasValue && ev.Rows.HasValue && onResizeCallback != null)
                        {
                            await onResizeCallback(ev.Cols.Value, ev.Rows.Value);
                        }
                        break;
                    case "marker":
                        if (!string.IsNullOrEmpty(ev.MarkerName) && onMarkerCallback != null)
                        {
                            await onMarkerCallback(ev.MarkerName);
                        }
                        break;
                    case "input":
                        if (!string.IsNullOrEmpty(ev.Input) && onInputCallback != null)
                        {
                            await onInputCallback(ev.Input);
                        }
                        break;
                    case "snapshot":
                        if (ev.Snapshot != null && onSnapshotCallback != null)
                        {
                            await onSnapshotCallback(ev.Snapshot);
                        }
                        break;
                }
                return lastOffset;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReplayRunner] Skip malformed V2 line: {ex.Message}");
                return lastOffset;
            }
        }

        private async Task<long> ProcessV1Line(string line, Func<byte[], Task> onDataCallback, bool realtime, long lastOffset)
        {
            try
            {
                // Legacy V1 parsing logic: {"t":TIK,"d":"DATA"}
                int tIndex = line.IndexOf("\"t\":");
                int dIndex = line.IndexOf("\"d\":");
                if (tIndex == -1 || dIndex == -1) return lastOffset;

                // Extract time
                int tStart = tIndex + 4;
                int tEnd = line.IndexOf(',', tStart);
                if (tEnd == -1) tEnd = line.IndexOf('}', tStart);
                string tStr = line.Substring(tStart, tEnd - tStart);
                long timeMs = long.Parse(tStr);

                // Extract data
                int dValueStart = line.IndexOf('"', dIndex + 4) + 1; // Find the " after "d":
                int dValueEnd = line.LastIndexOf('"');
                if (dValueEnd <= dValueStart) return lastOffset;

                string dataBase64 = line.Substring(dValueStart, dValueEnd - dValueStart);

                if (realtime)
                {
                    long delay = timeMs - lastOffset;
                    if (delay > 0) await Task.Delay((int)delay);
                    lastOffset = timeMs;
                }

                byte[] data = Convert.FromBase64String(dataBase64);
                await onDataCallback(data);
                return lastOffset;
            }
            catch
            {
                // Skip malformed V1 lines
                return lastOffset;
            }
        }
    }
}
