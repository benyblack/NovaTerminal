using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Generic;
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
            if (_cts.IsCancellationRequested || _queue.IsAddingCompleted) return;

            byte[] copy = new byte[length];
            Array.Copy(data, copy, length);

            TryEnqueue(new ReplayEvent
            {
                TimeOffsetMs = GetTimestamp(),
                Type = "data",
                Data = Convert.ToBase64String(copy)
            });
        }

        public void RecordResize(int cols, int rows)
        {
            if (_cts.IsCancellationRequested || _queue.IsAddingCompleted) return;

            TryEnqueue(new ReplayEvent
            {
                TimeOffsetMs = GetTimestamp(),
                Type = "resize",
                Cols = cols,
                Rows = rows
            });
        }

        public void RecordInput(string input)
        {
            if (_cts.IsCancellationRequested || _queue.IsAddingCompleted) return;
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(input);

            TryEnqueue(new ReplayEvent
            {
                TimeOffsetMs = GetTimestamp(),
                Type = "input",
                Data = Convert.ToBase64String(utf8),
                Input = input // Legacy fallback for old readers
            });
        }

        public void RecordSnapshot(TerminalBuffer buffer)
        {
            if (_cts.IsCancellationRequested || _queue.IsAddingCompleted) return;

            ReplaySnapshot snapshot;
            buffer.Lock.EnterReadLock();
            try
            {
                var rows = buffer.ViewportRows;
                int cols = buffer.Cols;
                int rowCount = buffer.Rows;

                snapshot = new ReplaySnapshot
                {
                    Cols = cols,
                    Rows = rowCount,
                    CursorCol = buffer.InternalCursorCol,
                    CursorRow = buffer.InternalCursorRow,
                    IsAltScreen = buffer.IsAltScreenActive,
                    ScrollTop = buffer.ScrollTop,
                    ScrollBottom = buffer.ScrollBottom,

                    IsAutoWrapMode = buffer.Modes.IsAutoWrapMode,
                    IsApplicationCursorKeys = buffer.Modes.IsApplicationCursorKeys,
                    IsOriginMode = buffer.Modes.IsOriginMode,
                    IsBracketedPasteMode = buffer.Modes.IsBracketedPasteMode,
                    IsCursorVisible = buffer.Modes.IsCursorVisible,

                    CurrentForeground = buffer.CurrentForeground.ToUint(),
                    CurrentBackground = buffer.CurrentBackground.ToUint(),
                    CurrentFgIndex = buffer.CurrentFgIndex,
                    CurrentBgIndex = buffer.CurrentBgIndex,
                    IsDefaultForeground = buffer.IsDefaultForeground,
                    IsDefaultBackground = buffer.IsDefaultBackground,
                    IsInverse = buffer.IsInverse,
                    IsBold = buffer.IsBold,
                    IsFaint = buffer.IsFaint,
                    IsItalic = buffer.IsItalic,
                    IsUnderline = buffer.IsUnderline,
                    IsBlink = buffer.IsBlink,
                    IsStrikethrough = buffer.IsStrikethrough,
                    IsHidden = buffer.IsHidden,
                    ExtendedText = new Dictionary<int, string>(),
                    RowWraps = new bool[rowCount]
                };

                // Allocate a single array for all cells in viewport
                TerminalCell[] allCells = new TerminalCell[cols * rowCount];

                for (int r = 0; r < rowCount; r++)
                {
                    var row = rows[r];
                    snapshot.RowWraps[r] = row.IsWrapped;

                    // Copy cells
                    Array.Copy(row.Cells, 0, allCells, r * cols, cols);

                    // Capture extended text
                    for (int c = 0; c < cols; c++)
                    {
                        if (row.Cells[c].HasExtendedText)
                        {
                            string? text = row.GetExtendedText(c);
                            if (text != null)
                            {
                                snapshot.ExtendedText[r * cols + c] = text;
                            }
                        }
                    }
                }

                // Convert allCells to Base64 using zero-copy casting
                var byteSpan = MemoryMarshal.AsBytes(allCells.AsSpan());
                snapshot.CellsBase64 = Convert.ToBase64String(byteSpan);
                snapshot.CellsSizeOf = Unsafe.SizeOf<TerminalCell>();
                snapshot.CellsLayoutId = TerminalCell.TerminalCellLayoutId;
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            TryEnqueue(new ReplayEvent
            {
                TimeOffsetMs = GetTimestamp(),
                Type = "snapshot",
                Snapshot = snapshot
            });
        }

        public void RecordMarker(string name)
        {
            if (_cts.IsCancellationRequested || _queue.IsAddingCompleted) return;

            TryEnqueue(new ReplayEvent
            {
                TimeOffsetMs = GetTimestamp(),
                Type = "marker",
                MarkerName = name
            });
        }

        private long GetTimestamp() => (long)(DateTime.UtcNow - _startTime).TotalMilliseconds;

        private void TryEnqueue(ReplayEvent ev)
        {
            try
            {
                _queue.Add(ev);
            }
            catch (InvalidOperationException)
            {
                // Queue completed during shutdown; best-effort recording only.
            }
        }

        private static int ReadEnvInt(string name, int defaultValue)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (!int.TryParse(raw, out int parsed) || parsed <= 0)
            {
                return defaultValue;
            }

            return parsed;
        }

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
            if (!_queue.IsAddingCompleted)
            {
                _queue.CompleteAdding();
            }

            int flushMs = ReadEnvInt("NOVATERM_RECORDER_FLUSH_MS", 10000);
            try
            {
                if (!_writeTask.Wait(flushMs))
                {
                    _cts.Cancel();
                    _writeTask.Wait(1000);
                }
            }
            catch { }

            _cts.Dispose();
        }
    }
}
