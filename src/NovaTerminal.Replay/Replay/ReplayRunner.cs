using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Core.Replay
{
    public enum ReplayPlaybackMode
    {
        Realtime,
        Virtual
    }

    public sealed class ReplayRunOptions
    {
        public ReplayPlaybackMode PlaybackMode { get; init; } = ReplayPlaybackMode.Virtual;
    }

    public sealed class ReplayRunResult
    {
        public bool Truncated { get; internal set; }
        public long LastGoodOffset { get; internal set; } = -1;
        public int EventsProcessed { get; internal set; }
    }

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
            long startOffsetBytes = 0,
            CancellationToken ct = default)
        {
            ReplayPlaybackMode playbackMode = realtime ? ReplayPlaybackMode.Realtime : ReplayPlaybackMode.Virtual;
            var options = new ReplayRunOptions { PlaybackMode = playbackMode };
            _ = await RunWithResultAsync(
                onDataCallback,
                onResizeCallback,
                onMarkerCallback,
                onInputCallback,
                onSnapshotCallback,
                onTimeUpdate,
                options,
                minTimeMs,
                fastForwardToMs,
                playbackSpeed,
                startOffsetBytes,
                ct);
        }

        public async Task<ReplayRunResult> RunWithResultAsync(
            Func<byte[], Task> onDataCallback,
            Func<int, int, Task>? onResizeCallback = null,
            Func<string, Task>? onMarkerCallback = null,
            Func<string, Task>? onInputCallback = null,
            Func<ReplaySnapshot, Task>? onSnapshotCallback = null,
            Func<long, Task>? onTimeUpdate = null,
            ReplayRunOptions? options = null,
            long minTimeMs = 0,
            long fastForwardToMs = 0,
            double playbackSpeed = 1.0,
            long startOffsetBytes = 0,
            CancellationToken ct = default)
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException("Replay file not found", _filePath);

            var result = new ReplayRunResult();
            ReplayPlaybackMode playbackMode = options?.PlaybackMode ?? ReplayPlaybackMode.Virtual;

            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line = await reader.ReadLineAsync();
            if (line == null) return result;

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

            if (startOffsetBytes > 0)
            {
                fs.Seek(startOffsetBytes, SeekOrigin.Begin);
                reader.DiscardBufferedData();
                line = await reader.ReadLineAsync();
                skipCurrentLine = false; // We already skipped what we needed to via Seek
            }

            // Use MinTimeMs if > 0, otherwise start from 0
            if (minTimeMs > 0 && startOffsetBytes == 0)
            {
                // If we have a minTime, we assume the caller has already set the state 
                // to what it was at minTimeMs (e.g. via snapshot).
                // So we should set lastOffset to minTimeMs to avoid huge initial delays.
                lastOffset = minTimeMs;
            }

            string? nextLine = await reader.ReadLineAsync();
            while (line != null && !ct.IsCancellationRequested)
            {
                if (!skipCurrentLine)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        bool isLastLine = nextLine == null;
                        try
                        {
                            ProcessedLine processedLine = isV2
                                ? await ProcessV2Line(line, onDataCallback, onResizeCallback, onMarkerCallback, onInputCallback, onSnapshotCallback,
                                    playbackMode, lastOffset, minTimeMs, fastForwardToMs, playbackSpeed)
                                : await ProcessV1Line(line, onDataCallback, playbackMode, lastOffset, minTimeMs, fastForwardToMs, playbackSpeed);

                            lastOffset = processedLine.LastOffsetMs;
                            if (processedLine.EventProcessed)
                            {
                                result.EventsProcessed++;
                                result.LastGoodOffset = lastOffset;
                            }
                            if (onTimeUpdate != null)
                            {
                                await onTimeUpdate(lastOffset);
                            }
                        }
                        catch (Exception ex) when (isLastLine && IsTailTruncationException(ex))
                        {
                            result.Truncated = true;
                            break;
                        }
                    }
                }
                skipCurrentLine = false;
                line = nextLine;
                nextLine = await reader.ReadLineAsync();
            }

            return result;
        }

        private readonly struct ProcessedLine
        {
            public ProcessedLine(long lastOffsetMs, bool eventProcessed)
            {
                LastOffsetMs = lastOffsetMs;
                EventProcessed = eventProcessed;
            }

            public long LastOffsetMs { get; }
            public bool EventProcessed { get; }
        }

        private async Task<ProcessedLine> ProcessV2Line(
            string line,
            Func<byte[], Task> onDataCallback,
            Func<int, int, Task>? onResizeCallback,
            Func<string, Task>? onMarkerCallback,
            Func<string, Task>? onInputCallback,
            Func<ReplaySnapshot, Task>? onSnapshotCallback,
            ReplayPlaybackMode playbackMode,
            long lastOffset,
            long minTimeMs,
            long fastForwardToMs,
            double playbackSpeed)
        {
            var ev = JsonSerializer.Deserialize(line, ReplayJsonContext.Default.ReplayEvent)
                     ?? throw new JsonException("Replay event line deserialized to null.");

            // SKIP logic: If event is strictly before minTimeMs, ignore it completely (unless it's a resize? No, caller handles state).
            if (ev.TimeOffsetMs < minTimeMs) return new ProcessedLine(lastOffset, eventProcessed: false);

            long updatedOffset = await AdvanceClockAsync(ev.TimeOffsetMs, playbackMode, lastOffset, fastForwardToMs, playbackSpeed);

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
                    if (ev.Snapshot != null)
                    {
                        ValidateSnapshotCellLayout(ev.Snapshot);
                        if (onSnapshotCallback != null)
                        {
                            await onSnapshotCallback(ev.Snapshot);
                        }
                    }
                    break;
            }

            return new ProcessedLine(updatedOffset, eventProcessed: true);
        }

        private static void ValidateSnapshotCellLayout(ReplaySnapshot snapshot)
        {
            bool hasAnyLayoutMetadata = snapshot.CellsSizeOf.HasValue || snapshot.CellsLayoutId != null;
            if (!hasAnyLayoutMetadata)
            {
                // Legacy recordings may not carry layout metadata.
                return;
            }

            int expectedSizeOf = Unsafe.SizeOf<TerminalCell>();
            string expectedLayoutId = TerminalCell.TerminalCellLayoutId;
            int? actualSizeOf = snapshot.CellsSizeOf;
            string? actualLayoutId = snapshot.CellsLayoutId;

            bool sizeMatches = actualSizeOf.HasValue && actualSizeOf.Value == expectedSizeOf;
            bool layoutMatches = string.Equals(actualLayoutId, expectedLayoutId, StringComparison.Ordinal);
            if (sizeMatches && layoutMatches)
            {
                return;
            }

            string actualSizeLabel = actualSizeOf.HasValue ? actualSizeOf.Value.ToString() : "<missing>";
            string actualLayoutLabel = string.IsNullOrEmpty(actualLayoutId) ? "<missing>" : actualLayoutId;
            throw new InvalidDataException(
                $"cell layout mismatch: expected cells_sizeof={expectedSizeOf}, cells_layout_id={expectedLayoutId}; actual cells_sizeof={actualSizeLabel}, cells_layout_id={actualLayoutLabel}.");
        }

        private async Task<ProcessedLine> ProcessV1Line(
            string line,
            Func<byte[], Task> onDataCallback,
            ReplayPlaybackMode playbackMode,
            long lastOffset,
            long minTimeMs,
            long fastForwardToMs,
            double playbackSpeed)
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Legacy replay line must be a JSON object.");
            }

            if (!root.TryGetProperty("t", out JsonElement timeElement) || !timeElement.TryGetInt64(out long timeMs))
            {
                throw new JsonException("Legacy replay line missing numeric 't' field.");
            }

            if (timeMs < minTimeMs)
            {
                return new ProcessedLine(lastOffset, eventProcessed: false);
            }

            if (!root.TryGetProperty("d", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("Legacy replay line missing string 'd' field.");
            }

            string dataBase64 = dataElement.GetString() ?? throw new JsonException("Legacy replay data field is null.");

            long updatedOffset = await AdvanceClockAsync(timeMs, playbackMode, lastOffset, fastForwardToMs, playbackSpeed);
            byte[] data = Convert.FromBase64String(dataBase64);
            await onDataCallback(data);
            return new ProcessedLine(updatedOffset, eventProcessed: true);
        }

        private static async Task<long> AdvanceClockAsync(
            long eventOffsetMs,
            ReplayPlaybackMode playbackMode,
            long lastOffsetMs,
            long fastForwardToMs,
            double playbackSpeed)
        {
            if (playbackMode == ReplayPlaybackMode.Virtual)
            {
                return eventOffsetMs;
            }

            bool isFastForwarding = eventOffsetMs < fastForwardToMs;
            if (isFastForwarding)
            {
                return eventOffsetMs;
            }

            if (lastOffsetMs == -1)
            {
                return eventOffsetMs;
            }

            long delay = eventOffsetMs - lastOffsetMs;
            if (delay > 0)
            {
                int outputDelay = (int)(delay / playbackSpeed);
                if (outputDelay > 0)
                {
                    await Task.Delay(outputDelay);
                }
            }

            return eventOffsetMs;
        }

        private static bool IsTailTruncationException(Exception ex)
        {
            return ex is JsonException || ex is FormatException;
        }
    }
}
