using Xunit;
using NovaTerminal.Core;
using NovaTerminal.Core.Storage;

namespace NovaTerminal.Tests.Buffer
{
    public class TerminalMemoryMetricsTests
    {
        [Fact]
        public void GetMemoryMetrics_ReturnsReasonableValues()
        {
            var buffer = new TerminalBuffer(80, 24);
            
            // Add some scrollback
            var row = new TerminalCell[80];
            for (int i = 0; i < 100; i++)
            {
                buffer.Scrollback.AppendRow(row, false);
            }

            var metrics = buffer.GetMemoryMetrics(glyphCacheEntries: 50, glyphCacheAtlasBytes: 1024 * 1024);

            Assert.True(metrics.ScrollbackBytes > 0);
            Assert.True(metrics.ActivePages > 0);
            Assert.Equal(80 * 24, metrics.ViewportCells);
            Assert.Equal(50, metrics.GlyphCacheEntries);
            Assert.Equal(1024 * 1024, metrics.GlyphCacheAtlasBytes);
        }
    }
}
