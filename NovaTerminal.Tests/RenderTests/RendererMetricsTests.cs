using NovaTerminal.Core;
using System.Threading.Tasks;
using Xunit;

namespace NovaTerminal.Tests.RenderTests
{
    public class RendererMetricsTests
    {
        [Fact]
        [Trait("Category", "RenderMetrics")]
        public void Statistics_AreInitiallyZero()
        {
            RendererStatistics.Reset();
            Assert.Equal(0, RendererStatistics.TotalFrames);
        }

        [Fact]
        [Trait("Category", "RenderMetrics")]
        public void RecordFrame_IncrementsCounters()
        {
            RendererStatistics.Reset();
            RendererStatistics.RecordFrame(true, 100);
            
            Assert.Equal(1, RendererStatistics.TotalFrames);
            Assert.Equal(1, RendererStatistics.FullRedraws);
            Assert.Equal(100, RendererStatistics.DirtyCellsRendered);
        }

        // Note: We cannot easily unit test the actual TerminalView rendering loop here 
        // because it requires a valid Dispatcher and Rendering Context which are hard to mock in headless xUnit.
        // However, we verify the collector logic works. 
        // Real integration tests would need a UI test framework (Headless Avalonia).
    }
}
