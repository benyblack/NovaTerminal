using System.Buffers;
using System.Text.Json;
using System.Threading;

namespace NovaTerminal.Core
{
    public sealed class RenderPerfWriter : IDisposable
    {
        private const int FlushEveryFrames = 60;
        private const string EnabledEnvVar = "NOVATERM_RENDER_METRICS";
        private const string OutputEnvVar = "NOVATERM_RENDER_METRICS_OUT";
        private readonly object _gate = new();
        private readonly FileStream _stream;
        private readonly ArrayBufferWriter<byte> _buffer = new(512);
        private long _frameIndex;
        private int _pendingFramesSinceFlush;
        private bool _disabled;

        private RenderPerfWriter(FileStream stream)
        {
            _stream = stream;
        }

        public static RenderPerfWriter? CreateFromEnvironment()
        {
            if (!IsEnabled())
            {
                return null;
            }

            string? configuredPath = Environment.GetEnvironmentVariable(OutputEnvVar);
            string outputPath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(AppContext.BaseDirectory, "render_metrics.jsonl")
                : configuredPath;

            return Create(outputPath);
        }

        public static RenderPerfWriter? Create(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return null;
            }

            try
            {
                string fullPath = Path.GetFullPath(outputPath);
                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var stream = new FileStream(
                    fullPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: FileOptions.SequentialScan);

                return new RenderPerfWriter(stream);
            }
            catch
            {
                return null;
            }
        }

        public long NextFrameIndex() => Interlocked.Increment(ref _frameIndex);

        public void TryWrite(RenderPerfMetrics metrics)
        {
            if (_disabled)
            {
                return;
            }

            lock (_gate)
            {
                if (_disabled)
                {
                    return;
                }

                try
                {
                    _buffer.Clear();
                    using (var writer = new Utf8JsonWriter(_buffer))
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber(nameof(RenderPerfMetrics.FrameIndex), metrics.FrameIndex);
                        writer.WriteNumber(nameof(RenderPerfMetrics.FrameTimeMs), metrics.FrameTimeMs);
                        writer.WriteNumber(nameof(RenderPerfMetrics.DirtyRows), metrics.DirtyRows);
                        writer.WriteNumber(nameof(RenderPerfMetrics.DirtySpansTotal), metrics.DirtySpansTotal);
                        writer.WriteNumber(nameof(RenderPerfMetrics.DirtySpanCount), metrics.DirtySpanCount);
                        writer.WriteNumber(nameof(RenderPerfMetrics.SpanRenderCount), metrics.SpanRenderCount);
                        writer.WriteNumber(nameof(RenderPerfMetrics.RowRenderCount), metrics.RowRenderCount);
                        writer.WriteNumber(nameof(RenderPerfMetrics.DirtyCellsEstimated), metrics.DirtyCellsEstimated);
                        writer.WriteNumber(nameof(RenderPerfMetrics.DrawCallsText), metrics.DrawCallsText);
                        writer.WriteNumber(nameof(RenderPerfMetrics.DrawCallsRects), metrics.DrawCallsRects);
                        writer.WriteNumber(nameof(RenderPerfMetrics.DrawCallsTotal), metrics.DrawCallsTotal);
                        writer.WriteNumber(nameof(RenderPerfMetrics.RowPictureCacheHits), metrics.RowPictureCacheHits);
                        writer.WriteNumber(nameof(RenderPerfMetrics.RowPictureCacheMisses), metrics.RowPictureCacheMisses);
                        writer.WriteNumber(nameof(RenderPerfMetrics.PictureBuilds), metrics.PictureBuilds);
                        writer.WriteNumber(nameof(RenderPerfMetrics.FlushCount), metrics.FlushCount);
                        writer.WriteNumber(nameof(RenderPerfMetrics.AtlasAlphaGlyphs), metrics.AtlasAlphaGlyphs);
                        writer.WriteNumber(nameof(RenderPerfMetrics.AtlasColorGlyphs), metrics.AtlasColorGlyphs);
                        writer.WriteNumber(nameof(RenderPerfMetrics.DirectDrawTextCount), metrics.DirectDrawTextCount);
                        writer.WriteNumber(nameof(RenderPerfMetrics.ShapedTextRuns), metrics.ShapedTextRuns);
                        writer.WriteNumber(nameof(RenderPerfMetrics.AllocBytesThisFrame), metrics.AllocBytesThisFrame);
                        writer.WriteEndObject();
                        writer.Flush();
                    }

                    _stream.Write(_buffer.WrittenSpan);
                    _stream.WriteByte((byte)'\n');
                    _pendingFramesSinceFlush++;
                    if (_pendingFramesSinceFlush >= FlushEveryFrames)
                    {
                        FlushUnsafe();
                    }
                }
                catch
                {
                    DisableUnsafe();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                DisableUnsafe();
            }
        }

        private static bool IsEnabled()
        {
            string? raw = Environment.GetEnvironmentVariable(EnabledEnvVar);
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        private void DisableUnsafe()
        {
            if (_disabled)
            {
                return;
            }

            _disabled = true;
            try
            {
                FlushUnsafe();
                _stream.Dispose();
            }
            catch
            {
                // Keep failures non-fatal.
            }
        }

        private void FlushUnsafe()
        {
            _stream.Flush();
            _pendingFramesSinceFlush = 0;
        }
    }
}
