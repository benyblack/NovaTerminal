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
        private static long _ptyQueueDrops;
        private static long _ptyQueueMaxDepth;
        private static long _resizeDispatchLatencyMs;
        private static long _resizeDispatchSamples;
        private static long _tabSwitchTimeMs;
        private static long _tabSwitchSamples;
        private static long _tabVisualUpdateTimeMs;
        private static long _tabVisualUpdateSamples;
        private static long _tabAutomationUpdateTimeMs;
        private static long _tabAutomationUpdateSamples;
        private static long _sessionSaveTimeMs;
        private static long _sessionSaveSamples;
        private static long _sessionSaveBytes;
        private static long _sessionRestoreTimeMs;
        private static long _sessionRestoreSamples;
        private static long _sessionRestoreBytes;
        private static long _terminalViewActiveTimerCount;
        private static long _terminalViewPeakTimerCount;
        private static long _hiddenInvalidationRequests;

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
        public static long PtyQueueDrops => Interlocked.Read(ref _ptyQueueDrops);
        public static long PtyQueueMaxDepth => Interlocked.Read(ref _ptyQueueMaxDepth);
        public static long ResizeDispatchLatencyMs => Interlocked.Read(ref _resizeDispatchLatencyMs);
        public static long ResizeDispatchSamples => Interlocked.Read(ref _resizeDispatchSamples);
        public static long TabSwitchTimeMs => Interlocked.Read(ref _tabSwitchTimeMs);
        public static long TabSwitchSamples => Interlocked.Read(ref _tabSwitchSamples);
        public static long TabVisualUpdateTimeMs => Interlocked.Read(ref _tabVisualUpdateTimeMs);
        public static long TabVisualUpdateSamples => Interlocked.Read(ref _tabVisualUpdateSamples);
        public static long TabAutomationUpdateTimeMs => Interlocked.Read(ref _tabAutomationUpdateTimeMs);
        public static long TabAutomationUpdateSamples => Interlocked.Read(ref _tabAutomationUpdateSamples);
        public static long SessionSaveTimeMs => Interlocked.Read(ref _sessionSaveTimeMs);
        public static long SessionSaveSamples => Interlocked.Read(ref _sessionSaveSamples);
        public static long SessionSaveBytes => Interlocked.Read(ref _sessionSaveBytes);
        public static long SessionRestoreTimeMs => Interlocked.Read(ref _sessionRestoreTimeMs);
        public static long SessionRestoreSamples => Interlocked.Read(ref _sessionRestoreSamples);
        public static long SessionRestoreBytes => Interlocked.Read(ref _sessionRestoreBytes);
        public static long TerminalViewActiveTimerCount => Interlocked.Read(ref _terminalViewActiveTimerCount);
        public static long TerminalViewPeakTimerCount => Interlocked.Read(ref _terminalViewPeakTimerCount);
        public static long HiddenInvalidationRequests => Interlocked.Read(ref _hiddenInvalidationRequests);

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

        public static void RecordBufferReadLockTimeMs(long ms)
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
        public static void RecordPtyQueueDrop() => Interlocked.Increment(ref _ptyQueueDrops);
        public static void RecordResizeDispatchLatency(long ms)
        {
            Interlocked.Add(ref _resizeDispatchLatencyMs, ms);
            Interlocked.Increment(ref _resizeDispatchSamples);
        }

        public static void RecordPtyQueueDepth(int depth)
        {
            long d = depth;
            long current = Interlocked.Read(ref _ptyQueueMaxDepth);
            while (d > current)
            {
                long previous = Interlocked.CompareExchange(ref _ptyQueueMaxDepth, d, current);
                if (previous == current) break;
                current = previous;
            }
        }

        public static void RecordTabSwitchTime(long ms)
        {
            Interlocked.Add(ref _tabSwitchTimeMs, ms);
            Interlocked.Increment(ref _tabSwitchSamples);
        }

        public static void RecordTabVisualUpdateTime(long ms)
        {
            Interlocked.Add(ref _tabVisualUpdateTimeMs, ms);
            Interlocked.Increment(ref _tabVisualUpdateSamples);
        }

        public static void RecordTabAutomationUpdateTime(long ms)
        {
            Interlocked.Add(ref _tabAutomationUpdateTimeMs, ms);
            Interlocked.Increment(ref _tabAutomationUpdateSamples);
        }

        public static void RecordSessionSave(long ms, int bytes)
        {
            Interlocked.Add(ref _sessionSaveTimeMs, ms);
            Interlocked.Increment(ref _sessionSaveSamples);
            Interlocked.Add(ref _sessionSaveBytes, bytes);
        }

        public static void RecordSessionRestore(long ms, int bytes)
        {
            Interlocked.Add(ref _sessionRestoreTimeMs, ms);
            Interlocked.Increment(ref _sessionRestoreSamples);
            Interlocked.Add(ref _sessionRestoreBytes, bytes);
        }

        public static void RecordTerminalViewTimersStarted()
        {
            long active = Interlocked.Increment(ref _terminalViewActiveTimerCount);
            long peak = Interlocked.Read(ref _terminalViewPeakTimerCount);
            while (active > peak)
            {
                long previous = Interlocked.CompareExchange(ref _terminalViewPeakTimerCount, active, peak);
                if (previous == peak) break;
                peak = previous;
            }
        }

        public static void RecordTerminalViewTimersStopped()
        {
            while (true)
            {
                long current = Interlocked.Read(ref _terminalViewActiveTimerCount);
                if (current <= 0)
                {
                    if (current < 0)
                    {
                        Interlocked.Exchange(ref _terminalViewActiveTimerCount, 0);
                    }
                    return;
                }

                if (Interlocked.CompareExchange(ref _terminalViewActiveTimerCount, current - 1, current) == current)
                {
                    return;
                }
            }
        }

        public static void RecordHiddenInvalidationRequest()
        {
            Interlocked.Increment(ref _hiddenInvalidationRequests);
        }

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
            Interlocked.Exchange(ref _ptyQueueDrops, 0);
            Interlocked.Exchange(ref _ptyQueueMaxDepth, 0);
            Interlocked.Exchange(ref _resizeDispatchLatencyMs, 0);
            Interlocked.Exchange(ref _resizeDispatchSamples, 0);
            Interlocked.Exchange(ref _tabSwitchTimeMs, 0);
            Interlocked.Exchange(ref _tabSwitchSamples, 0);
            Interlocked.Exchange(ref _tabVisualUpdateTimeMs, 0);
            Interlocked.Exchange(ref _tabVisualUpdateSamples, 0);
            Interlocked.Exchange(ref _tabAutomationUpdateTimeMs, 0);
            Interlocked.Exchange(ref _tabAutomationUpdateSamples, 0);
            Interlocked.Exchange(ref _sessionSaveTimeMs, 0);
            Interlocked.Exchange(ref _sessionSaveSamples, 0);
            Interlocked.Exchange(ref _sessionSaveBytes, 0);
            Interlocked.Exchange(ref _sessionRestoreTimeMs, 0);
            Interlocked.Exchange(ref _sessionRestoreSamples, 0);
            Interlocked.Exchange(ref _sessionRestoreBytes, 0);
            Interlocked.Exchange(ref _terminalViewActiveTimerCount, 0);
            Interlocked.Exchange(ref _terminalViewPeakTimerCount, 0);
            Interlocked.Exchange(ref _hiddenInvalidationRequests, 0);
        }

        public static string GetReport()
        {
            long resizeSamples = ResizeDispatchSamples;
            long avgResizeDispatch = resizeSamples > 0 ? ResizeDispatchLatencyMs / resizeSamples : 0;
            long tabSwitchAvg = TabSwitchSamples > 0 ? TabSwitchTimeMs / TabSwitchSamples : 0;
            long tabVisualAvg = TabVisualUpdateSamples > 0 ? TabVisualUpdateTimeMs / TabVisualUpdateSamples : 0;
            long tabAutomationAvg = TabAutomationUpdateSamples > 0 ? TabAutomationUpdateTimeMs / TabAutomationUpdateSamples : 0;
            long sessionSaveAvg = SessionSaveSamples > 0 ? SessionSaveTimeMs / SessionSaveSamples : 0;
            long sessionRestoreAvg = SessionRestoreSamples > 0 ? SessionRestoreTimeMs / SessionRestoreSamples : 0;
            return $"Frames: {TotalFrames}, Full: {FullRedraws}, Dirty: {DirtyCellsRendered}, LockMs: {BufferReadLockTimeMs}, Hits: {RowCacheHits}, Misses: {RowCacheMisses}, Snaps: {RowSnapshotsTaken}, PicsRec: {RowPicturesRecorded}, RecTime: {RowPictureRecordTimeMs}ms, RenderTime: {FrameRenderTimeMs}ms, PtyDrops: {PtyQueueDrops}, PtyMaxQ: {PtyQueueMaxDepth}, ResizeDispatchAvg: {avgResizeDispatch}ms, TabSwitchAvg: {tabSwitchAvg}ms, TabVisualAvg: {tabVisualAvg}ms, TabAutomationAvg: {tabAutomationAvg}ms, SessionSaveAvg: {sessionSaveAvg}ms/{SessionSaveBytes}B, SessionRestoreAvg: {sessionRestoreAvg}ms/{SessionRestoreBytes}B, TvTimers: {TerminalViewActiveTimerCount}/{TerminalViewPeakTimerCount}, HiddenInvReq: {HiddenInvalidationRequests}";
        }
    }
}
