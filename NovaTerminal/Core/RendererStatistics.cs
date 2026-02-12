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

        public static long TotalFrames => Interlocked.Read(ref _totalFrames);
        public static long FullRedraws => Interlocked.Read(ref _fullRedraws);
        public static long DirtyCellsRendered => Interlocked.Read(ref _dirtyCellsRendered);
        public static long ParseTimeMs => Interlocked.Read(ref _parseTimeMs);
        public static long BytesProcessed => Interlocked.Read(ref _bytesProcessed);

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

        public static void Reset()
        {
            Interlocked.Exchange(ref _totalFrames, 0);
            Interlocked.Exchange(ref _fullRedraws, 0);
            Interlocked.Exchange(ref _dirtyCellsRendered, 0);
            Interlocked.Exchange(ref _parseTimeMs, 0);
            Interlocked.Exchange(ref _bytesProcessed, 0);
        }

        public static string GetReport()
        {
            return $"Frames: {TotalFrames}, Full: {FullRedraws}, DirtyCells: {DirtyCellsRendered}, ParseMs: {ParseTimeMs}, Bytes: {BytesProcessed}";
        }
    }
}
