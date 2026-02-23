using System;
using Xunit;
using NovaTerminal.Core;
using SkiaSharp;

namespace NovaTerminal.Tests
{
    public class CacheCollisionTests
    {
        [Fact]
        public void RowCache_CollisionOnRowShift_ReproducesGhosting()
        {
            // Simulate the RowImageCache logic from TerminalDrawOperation
            var cache = new RowImageCache();

            // Scenario: Row A is at Y=10, reaches revision 3
            var rowA = new TerminalRow(100, new TermColor(255, 255, 255), new TermColor(0, 0, 0));
            rowA.TouchRevision(); rowA.TouchRevision(); rowA.TouchRevision();
            Assert.Equal(3u, rowA.Revision);

            // Renderer creates a picture for Row A at Y=10
            using var recorder = new SKPictureRecorder();
            recorder.BeginRecording(new SKRect(0, 0, 100, 100));
            var picA = recorder.EndRecording();
            cache.Add(rowA.Id, rowA.Revision, picA);

            // Now a ScrollUp happens! Row B shifts into Y=10
            var rowB = new TerminalRow(100, new TermColor(255, 255, 255), new TermColor(0, 0, 0));

            // Row B was previously at Y=11 and had reached revision 2. 
            rowB.TouchRevision(); rowB.TouchRevision();

            // ScrollUp calls TouchRevision on Row B.
            rowB.TouchRevision();
            Assert.Equal(3u, rowB.Revision);

            // Renderer comes along to draw Y=10 (which is now Row B)
            // It asks the cache for (RowId, Revision=3)
            var retrieved = cache.Get(rowB.Id, rowB.Revision);

            // FIX VERIFICATION: The cache returns null because RowB has a totally unique RowId!
            // No more ghosting! 
            Assert.Null(retrieved);
        }
    }
}
