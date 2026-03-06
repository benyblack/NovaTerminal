using System;
using Xunit;
using NovaTerminal.Core;
using NovaTerminal.Core.Storage;

namespace NovaTerminal.Tests.Buffer
{
    public class ScrollbackPagesTests : IDisposable
    {
        private TerminalPagePool _pool;

        public ScrollbackPagesTests()
        {
            _pool = new TerminalPagePool();
        }

        public void Dispose()
        {
            _pool.Clear();
        }

        [Fact]
        public void GetRow_SequentialAccess_AvoidsON2Performance()
        {
            // Arrange: create a scrollback with 1000 pages (64,000 rows)
            int cols = 80;
            var scrollback = new ScrollbackPages(cols, _pool, maxScrollbackBytes: 256L * 1024 * 1024);
            
            var defCell = TerminalCell.Default;
            var rowArray = new TerminalCell[80];
            for (int i = 0; i < 80; i++) rowArray[i] = defCell;
            
            int totalRows = 64000;
            for (int i = 0; i < totalRows; i++)
            {
                // Assign a unique character to ensure we fetch the right row
                rowArray[0].Character = (char)('A' + (i % 26));
                scrollback.AppendRow(rowArray.AsSpan());
            }
            
            // Assert Count matches
            Assert.Equal(totalRows, scrollback.Count);

            // Act & Assert: Sequential access
            // This loop should be very fast due to the O(1) cache. 
            // If it were O(N^2), 64000 iterations over 1000 pages would take significant time and could time out.
            for (int i = 0; i < totalRows; i++)
            {
                var row = scrollback.GetRow(i);
                char expectedChar = (char)('A' + (i % 26));
                Assert.Equal(expectedChar, row[0].Character);
            }
        }
    }
}
