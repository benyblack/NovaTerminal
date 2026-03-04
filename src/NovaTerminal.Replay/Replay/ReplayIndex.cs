using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Core.Replay
{
    public sealed class ReplayIndexEntry
    {
        public long TimeMs { get; }
        public long ByteOffset { get; }

        public ReplayIndexEntry(long timeMs, long byteOffset)
        {
            TimeMs = timeMs;
            ByteOffset = byteOffset;
        }
    }

    public sealed class ReplayIndex
    {
        private readonly List<ReplayIndexEntry> _entries = new();
        public IReadOnlyList<ReplayIndexEntry> Entries => _entries;

        public long DurationMs => _entries.Count > 0 ? _entries[_entries.Count - 1].TimeMs - _entries[0].TimeMs : 0;
        public long StartTimeMs => _entries.Count > 0 ? _entries[0].TimeMs : 0;
        public long EndTimeMs => _entries.Count > 0 ? _entries[_entries.Count - 1].TimeMs : 0;

        private ReplayIndex(List<ReplayIndexEntry> entries)
        {
            _entries = entries;
        }

        public long GetClosestOffset(long targetTimeMs)
        {
            if (_entries.Count == 0) return 0;
            if (targetTimeMs <= _entries[0].TimeMs) return _entries[0].ByteOffset;
            if (targetTimeMs >= _entries[_entries.Count - 1].TimeMs) return _entries[_entries.Count - 1].ByteOffset;

            int left = 0;
            int right = _entries.Count - 1;
            int bestIndex = 0;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                if (_entries[mid].TimeMs == targetTimeMs)
                {
                    return _entries[mid].ByteOffset;
                }
                
                if (_entries[mid].TimeMs < targetTimeMs)
                {
                    bestIndex = mid;
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            return _entries[bestIndex].ByteOffset;
        }

        public static async Task<ReplayIndex> BuildAsync(string filePath, CancellationToken ct = default)
        {
            var entries = new List<ReplayIndexEntry>();
            if (!File.Exists(filePath)) return new ReplayIndex(entries);

            const int bufferSize = 81920; 
            byte[] buffer = new byte[bufferSize];

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, true);
            long absoluteOffset = 0;
            long lineStartOffset = 0;

            var lineBytes = new List<byte>(4096);

            int bytesRead;
            while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = buffer[i];
                    
                    if (b == '\n')
                    {
                        // Parse line bytes, excluding newline characters
                        if (lineBytes.Count > 0 && lineBytes[lineBytes.Count - 1] == '\r')
                        {
                            lineBytes.RemoveAt(lineBytes.Count - 1);
                        }

                        if (lineBytes.Count > 0)
                        {
                            long? timeMs = ExtractTimeMsFromLine(lineBytes.ToArray());
                            if (timeMs.HasValue)
                            {
                                entries.Add(new ReplayIndexEntry(timeMs.Value, lineStartOffset));
                            }
                        }

                        lineBytes.Clear();
                        lineStartOffset = absoluteOffset + i + 1;
                    }
                    else
                    {
                        lineBytes.Add(b);
                    }
                }
                absoluteOffset += bytesRead;
            }

            // Process final line if file doesn't end with newline
            if (lineBytes.Count > 0)
            {
                long? timeMs = ExtractTimeMsFromLine(lineBytes.ToArray());
                if (timeMs.HasValue)
                {
                    entries.Add(new ReplayIndexEntry(timeMs.Value, lineStartOffset));
                }
            }

            return new ReplayIndex(entries);
        }

        private static long? ExtractTimeMsFromLine(byte[] lineBytes)
        {
            try
            {
                var reader = new Utf8JsonReader(lineBytes);
                
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("t"u8))
                    {
                        if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                        {
                            return reader.GetInt64();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Incomplete or trailing JSON -> ignore this line
            }
            return null;
        }
    }
}
