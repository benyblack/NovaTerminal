using NovaTerminal.Core;
using System.Threading.Tasks;
using Xunit;
using NovaTerminal.Tests.Infra;

namespace NovaTerminal.Tests.RenderTests
{
    [Collection("RendererStatistics")]
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

        [Fact]
        [Trait("Category", "RenderMetrics")]
        public void TabAndSessionMetrics_AreRecorded()
        {
            RendererStatistics.Reset();

            RendererStatistics.RecordTabSwitchTime(12);
            RendererStatistics.RecordTabVisualUpdateTime(8);
            RendererStatistics.RecordTabAutomationUpdateTime(5);
            RendererStatistics.RecordSessionSave(3, 1200);
            RendererStatistics.RecordSessionRestore(4, 2200);

            Assert.Equal(12, RendererStatistics.TabSwitchTimeMs);
            Assert.Equal(1, RendererStatistics.TabSwitchSamples);
            Assert.Equal(8, RendererStatistics.TabVisualUpdateTimeMs);
            Assert.Equal(1, RendererStatistics.TabVisualUpdateSamples);
            Assert.Equal(5, RendererStatistics.TabAutomationUpdateTimeMs);
            Assert.Equal(1, RendererStatistics.TabAutomationUpdateSamples);
            Assert.Equal(3, RendererStatistics.SessionSaveTimeMs);
            Assert.Equal(1, RendererStatistics.SessionSaveSamples);
            Assert.Equal(1200, RendererStatistics.SessionSaveBytes);
            Assert.Equal(4, RendererStatistics.SessionRestoreTimeMs);
            Assert.Equal(1, RendererStatistics.SessionRestoreSamples);
            Assert.Equal(2200, RendererStatistics.SessionRestoreBytes);
        }

        // Note: We cannot easily unit test the actual TerminalView rendering loop here 
        // because it requires a valid Dispatcher and Rendering Context which are hard to mock in headless xUnit.
        // However, we verify the collector logic works. 
        // Real integration tests would need a UI test framework (Headless Avalonia).
    }
}
