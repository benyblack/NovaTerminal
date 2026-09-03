using Avalonia;
using Avalonia.Headless;
using NovaTerminal;

// Global configuration for Avalonia Headless testing
[assembly: AvaloniaTestApplication(typeof(NovaTerminal.Tests.TestAppBuilder))]

// Deliberately NOT AvaloniaTestIsolationLevel.PerAssembly, tempting though it looks: it would
// give one application, set up once on the session's dispatch thread and never torn down, which
// is exactly what the plain-[Fact] callers of SnapshotService.EnsureAvaloniaInitialized would
// like. It also hangs the suite. Between tests the session thread is parked on its work queue and
// never pumps the dispatcher, so any plain [Fact] that marshals onto Dispatcher.UIThread waits
// forever - SshInteractionServiceTests hangs even when run alone. The default PerTest isolation
// resets the dispatcher around every [AvaloniaFact], which is what lets those tests be their own
// UI thread and run the marshalled work inline.

namespace NovaTerminal.Tests
{
    public class TestAppBuilder
    {
        /// <remarks>
        /// <para>
        /// <c>UseHeadlessDrawing = false</c> routes drawing through the Skia backend registered above
        /// instead of the headless stub. The stub accepts every draw call and produces an empty raster,
        /// which is fine while nothing looks at pixels and useless the moment something does: it would
        /// make <c>CommandAssistOverlayContentRenderTests</c> pass on a blank image, certifying exactly
        /// the regression it exists to catch.
        /// </para>
        /// </remarks>
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            });
    }
}
