using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
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
            Func<long, Task>? onTimeUpdate = null,
            bool realtime = false,
            long minTimeMs = 0,
            long fastForwardToMs = 0,
            double playbackSpeed = 1.0,
            CancellationToken ct = default)
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

            long lastOffset = -1;
            // If it's V2, we skip the first line (header).
            // If it's V1, we process the first line immediately.
            bool skipCurrentLine = isV2;

            // Use MinTimeMs if > 0, otherwise start from 0
            if (minTimeMs > 0)
            {
                // If we have a minTime, we assume the caller has already set the state 
                // to what it was at minTimeMs (e.g. via snapshot).
                // So we should set lastOffset to minTimeMs to avoid huge initial delays.
                lastOffset = minTimeMs;
            }

            while (line != null && !ct.IsCancellationRequested)
            {
                if (!skipCurrentLine)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (isV2)
                        {
                            lastOffset = await ProcessV2Line(line, onDataCallback, onResizeCallback, onMarkerCallback, onInputCallback, onSnapshotCallback,
                                realtime, lastOffset, minTimeMs, fastForwardToMs, playbackSpeed);
                        }
                        else
                        {
                            lastOffset = await ProcessV1Line(line, onDataCallback, realtime, lastOffset, minTimeMs, fastForwardToMs, playbackSpeed);
                        }
                        if (onTimeUpdate != null) await onTimeUpdate(lastOffset);
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
            long lastOffset,
            long minTimeMs,
            long fastForwardToMs,
            double playbackSpeed)
        {
            try
            {
                var ev = JsonSerializer.Deserialize(line, ReplayJsonContext.Default.ReplayEvent);
                if (ev == null) return lastOffset;

                // SKIP logic: If event is strictly before minTimeMs, ignore it completely (unless it's a resize? No, caller handles state).
                if (ev.TimeOffsetMs < minTimeMs) return lastOffset;

                // FAST FORWARD logic: If event is before fastForwardToMs, process immediately (no delay).
                bool isFastForwarding = ev.TimeOffsetMs < fastForwardToMs;

                if (realtime && !isFastForwarding)
                {
                    if (lastOffset == -1)
                    {
                        // First event in playback: snap to it immediately, no delay
                        lastOffset = ev.TimeOffsetMs;
                    }

                    long delay = ev.TimeOffsetMs - lastOffset;
                    if (delay > 0)
                    {
                        // Apply playback speed
                        int outputDelay = (int)(delay / playbackSpeed);
                        if (outputDelay > 0) await Task.Delay(outputDelay);
                    }
                    lastOffset = ev.TimeOffsetMs;
                }
                else if (isFastForwarding)
                {
                    // Update lastOffset but don't sleep
                    lastOffset = ev.TimeOffsetMs;
                }
                else
                {
                    // Normal non-realtime (should behave like fast forward technically, but kept logic same as before)
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
                        if (onInputCallback != null)
                        {
                            if (!string.IsNullOrEmpty(ev.Data))
                            {
                                try
                                {
                                    string input = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ev.Data));
                                    await onInputCallback(input);
                                }
                                catch
                                {
                                    // Fallback to legacy field if decoding fails
                                    if (!string.IsNullOrEmpty(ev.Input))
                                    {
                                        await onInputCallback(ev.Input);
                                    }
                                }
                            }
                            else if (!string.IsNullOrEmpty(ev.Input))
                            {
                                await onInputCallback(ev.Input);
                            }
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

        private async Task<long> ProcessV1Line(string line, Func<byte[], Task> onDataCallback, bool realtime, long lastOffset, long minTimeMs, long fastForwardToMs, double playbackSpeed)
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

                if (timeMs < minTimeMs) return lastOffset;

                // Extract data
                int dValueStart = line.IndexOf('"', dIndex + 4) + 1; // Find the " after "d":
                int dValueEnd = line.LastIndexOf('"');
                if (dValueEnd <= dValueStart) return lastOffset;

                string dataBase64 = line.Substring(dValueStart, dValueEnd - dValueStart);

                bool isFastForwarding = timeMs < fastForwardToMs;

                if (realtime && !isFastForwarding)
                {
                    if (lastOffset == -1)
                    {
                        lastOffset = timeMs;
                    }

                    long delay = timeMs - lastOffset;
                    if (delay > 0)
                    {
                        int outputDelay = (int)(delay / playbackSpeed);
                        if (outputDelay > 0) await Task.Delay(outputDelay);
                    }
                    lastOffset = timeMs;
                }
                else
                {
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
