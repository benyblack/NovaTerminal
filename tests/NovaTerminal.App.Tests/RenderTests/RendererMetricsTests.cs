using NovaTerminal.Shell;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Rendering;
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

            MetricsArtifactWriter.WriteRendererStatisticsSnapshot("render_metrics");
        }

        [Fact]
        [Trait("Category", "RenderMetrics")]
        public void StartupMetrics_AreRecorded()
        {
            RendererStatistics.Reset();

            RendererStatistics.RecordStartupWindowShown(25);
            RendererStatistics.RecordStartupFirstTerminalReady(70);
            RendererStatistics.RecordStartupSessionRestoreComplete(140);
            RendererStatistics.RecordStartupDeferredWork(45);
            RendererStatistics.RecordStartupBackgroundRestore(60);

            Assert.Equal(25, RendererStatistics.StartupWindowShownTimeMs);
            Assert.Equal(1, RendererStatistics.StartupWindowShownSamples);
            Assert.Equal(70, RendererStatistics.StartupFirstTerminalReadyTimeMs);
            Assert.Equal(1, RendererStatistics.StartupFirstTerminalReadySamples);
            Assert.Equal(140, RendererStatistics.StartupSessionRestoreCompleteTimeMs);
            Assert.Equal(1, RendererStatistics.StartupSessionRestoreCompleteSamples);
            Assert.Equal(45, RendererStatistics.StartupDeferredWorkTimeMs);
            Assert.Equal(1, RendererStatistics.StartupDeferredWorkSamples);
            Assert.Equal(60, RendererStatistics.StartupBackgroundRestoreTimeMs);
            Assert.Equal(1, RendererStatistics.StartupBackgroundRestoreSamples);
        }

        [Fact]
        [Trait("Category", "RenderMetrics")]
        public void TerminalViewVisibilityMetrics_AreRecorded()
        {
            RendererStatistics.Reset();

            RendererStatistics.RecordTerminalViewTimersStarted();
            RendererStatistics.RecordTerminalViewTimersStarted();
            RendererStatistics.RecordTerminalViewTimersStopped();
            RendererStatistics.RecordHiddenInvalidationRequest();
            RendererStatistics.RecordHiddenInvalidationRequest();

            Assert.Equal(1, RendererStatistics.TerminalViewActiveTimerCount);
            Assert.Equal(2, RendererStatistics.TerminalViewPeakTimerCount);
            Assert.Equal(2, RendererStatistics.HiddenInvalidationRequests);

            MetricsArtifactWriter.WriteRendererStatisticsSnapshot("terminal_view_visibility_metrics");
        }

        // Note: We cannot easily unit test the actual TerminalView rendering loop here 
        // because it requires a valid Dispatcher and Rendering Context which are hard to mock in headless xUnit.
        // However, we verify the collector logic works. 
        // Real integration tests would need a UI test framework (Headless Avalonia).
    }
}
