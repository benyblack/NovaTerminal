using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    public class SnapshotAllocationTests
    {
        [Fact]
        public void CaptureRenderSnapshot_UnchangedFrames_AllocationsStayLowAfterWarmup()
        {
            const int cols = 120;
            const int rows = 40;
            const int warmupCaptures = 10;
            const int measuredCaptures = 200;
            const double perCaptureCeilingBytes = 24_000; // Should allow snapshot object overhead, but not per-row cell-array churn.

            var buffer = new TerminalBuffer(cols, rows);
            buffer.WriteContent(new string('A', cols));

            var req = new RenderSnapshotRequest
            {
                ViewportCols = cols,
                ViewportRows = rows,
                ScrollOffset = 0
            };

            for (int i = 0; i < warmupCaptures; i++)
            {
                _ = buffer.CaptureRenderSnapshot(req, out _);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long startAllocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < measuredCaptures; i++)
            {
                _ = buffer.CaptureRenderSnapshot(req, out _);
            }
            long totalAllocated = GC.GetAllocatedBytesForCurrentThread() - startAllocated;

            double perCapture = totalAllocated / (double)measuredCaptures;
            Assert.True(
                perCapture <= perCaptureCeilingBytes,
                $"Expected low steady-state allocations for unchanged frames, but measured {perCapture:F0} bytes/capture (total={totalAllocated}, captures={measuredCaptures}, ceiling={perCaptureCeilingBytes}).");
        }

        [Fact]
        public void CaptureRenderSnapshot_SingleRowMutation_AllocationIncreaseIsModest()
        {
            const int cols = 120;
            const int rows = 40;
            const int warmupCaptures = 10;
            const int measuredCaptures = 120;
            const double additionalPerCaptureCeilingBytes = 24_000; // One dirty row should not trigger full-frame linear allocation growth.

            var buffer = new TerminalBuffer(cols, rows);
            buffer.WriteContent(new string('B', cols));

            var req = new RenderSnapshotRequest
            {
                ViewportCols = cols,
                ViewportRows = rows,
                ScrollOffset = 0
            };

            for (int i = 0; i < warmupCaptures; i++)
            {
                _ = buffer.CaptureRenderSnapshot(req, out _);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long unchangedStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < measuredCaptures; i++)
            {
                _ = buffer.CaptureRenderSnapshot(req, out _);
            }
            long unchangedTotal = GC.GetAllocatedBytesForCurrentThread() - unchangedStart;
            double unchangedPerCapture = unchangedTotal / (double)measuredCaptures;

            int targetRow = rows / 2;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long changedStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < measuredCaptures; i++)
            {
                buffer.Lock.EnterWriteLock();
                try
                {
                    var row = buffer.GetRowAbsolute(targetRow);
                    Assert.NotNull(row);

                    int col = i % cols;
                    TerminalCell cell = row!.Cells[col];
                    cell.Character = (char)('a' + (i % 26));
                    row.Cells[col] = cell;
                    row.TouchRevision();
                }
                finally
                {
                    buffer.Lock.ExitWriteLock();
                }

                _ = buffer.CaptureRenderSnapshot(req, out _);
            }
            long changedTotal = GC.GetAllocatedBytesForCurrentThread() - changedStart;
            double changedPerCapture = changedTotal / (double)measuredCaptures;

            double increase = changedPerCapture - unchangedPerCapture;
            Assert.True(
                increase <= additionalPerCaptureCeilingBytes,
                $"Expected one-row mutations to cause only modest allocation growth, but increase was {increase:F0} bytes/capture (unchanged={unchangedPerCapture:F0}, changed={changedPerCapture:F0}, ceiling={additionalPerCaptureCeilingBytes}).");
        }
    }
}
