using System.Threading;

namespace NovaTerminal.Core
{
    public static class RendererStatistics
    {
        private static long _totalFrames;
        private static long _fullRedraws;
        private static long _dirtyCellsRendered;
        private static long _parseTimeMs;
        private static long _bytesProcessed;

        private static long _bufferReadLockTimeMs;
        private static long _backgroundScans;
        private static long _rowCacheHits;
        private static long _rowCacheMisses;
        private static long _rowSnapshotsTaken;
        private static long _rowPicturesRecorded;
        private static long _rowPictureRecordTimeMs;
        private static long _frameRenderTimeMs;

        public static long TotalFrames => Interlocked.Read(ref _totalFrames);
        public static long FullRedraws => Interlocked.Read(ref _fullRedraws);
        public static long DirtyCellsRendered => Interlocked.Read(ref _dirtyCellsRendered);
        public static long ParseTimeMs => Interlocked.Read(ref _parseTimeMs);
        public static long BytesProcessed => Interlocked.Read(ref _bytesProcessed);
        public static long BufferReadLockTimeMs => Interlocked.Read(ref _bufferReadLockTimeMs);
        public static long BackgroundScans => Interlocked.Read(ref _backgroundScans);
        public static long RowCacheHits => Interlocked.Read(ref _rowCacheHits);
        public static long RowCacheMisses => Interlocked.Read(ref _rowCacheMisses);
        public static long RowSnapshotsTaken => Interlocked.Read(ref _rowSnapshotsTaken);
        public static long RowPicturesRecorded => Interlocked.Read(ref _rowPicturesRecorded);
        public static long RowPictureRecordTimeMs => Interlocked.Read(ref _rowPictureRecordTimeMs);
        public static long FrameRenderTimeMs => Interlocked.Read(ref _frameRenderTimeMs);

        public static void RecordFrame(bool fullRedraw, int dirtyCells)
        {
            Interlocked.Increment(ref _totalFrames);
            if (fullRedraw) Interlocked.Increment(ref _fullRedraws);
            Interlocked.Add(ref _dirtyCellsRendered, dirtyCells);
        }

        public static void RecordParseTime(long ms)
        {
            Interlocked.Add(ref _parseTimeMs, ms);
        }

        public static void RecordBytes(long count)
        {
            Interlocked.Add(ref _bytesProcessed, count);
        }

        public static void RecordReadLockTime(long ms)
        {
            Interlocked.Add(ref _bufferReadLockTimeMs, ms);
        }

        public static void RecordBackgroundScan()
        {
            Interlocked.Increment(ref _backgroundScans);
        }

        public static void RecordRowCacheHit() => Interlocked.Increment(ref _rowCacheHits);
        public static void RecordRowCacheMiss() => Interlocked.Increment(ref _rowCacheMisses);
        public static void RecordRowSnapshot() => Interlocked.Increment(ref _rowSnapshotsTaken);
        public static void RecordRowPictureRecorded() => Interlocked.Increment(ref _rowPicturesRecorded);
        public static void RecordRowPictureRecordTime(long ms) => Interlocked.Add(ref _rowPictureRecordTimeMs, ms);
        public static void RecordFrameRenderTime(long ms) => Interlocked.Add(ref _frameRenderTimeMs, ms);

        public static void Reset()
        {
            Interlocked.Exchange(ref _totalFrames, 0);
            Interlocked.Exchange(ref _fullRedraws, 0);
            Interlocked.Exchange(ref _dirtyCellsRendered, 0);
            Interlocked.Exchange(ref _parseTimeMs, 0);
            Interlocked.Exchange(ref _bytesProcessed, 0);
            Interlocked.Exchange(ref _bufferReadLockTimeMs, 0);
            Interlocked.Exchange(ref _backgroundScans, 0);
            Interlocked.Exchange(ref _rowCacheHits, 0);
            Interlocked.Exchange(ref _rowCacheMisses, 0);
            Interlocked.Exchange(ref _rowSnapshotsTaken, 0);
            Interlocked.Exchange(ref _rowPicturesRecorded, 0);
            Interlocked.Exchange(ref _rowPictureRecordTimeMs, 0);
            Interlocked.Exchange(ref _frameRenderTimeMs, 0);
        }

        public static string GetReport()
        {
            return $"Frames: {TotalFrames}, Full: {FullRedraws}, Dirty: {DirtyCellsRendered}, LockMs: {BufferReadLockTimeMs}, Hits: {RowCacheHits}, Misses: {RowCacheMisses}, Snaps: {RowSnapshotsTaken}, PicsRec: {RowPicturesRecorded}, RecTime: {RowPictureRecordTimeMs}ms, RenderTime: {FrameRenderTimeMs}ms";
        }
    }
}
