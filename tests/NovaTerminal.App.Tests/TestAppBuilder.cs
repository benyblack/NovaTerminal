using Avalonia;
using Avalonia.Headless;
using NovaTerminal;

// Global configuration for Avalonia Headless testing
[assembly: AvaloniaTestApplication(typeof(NovaTerminal.Tests.TestAppBuilder))]

// One Application for the whole assembly, set up once on HeadlessUnitTestSession's dispatch
// thread and never torn down, instead of Avalonia's default PerTest isolation.
//
// PerTest wraps every [AvaloniaFact] in AvaloniaLocator.EnterScope() plus a
// Dispatcher.ResetBeforeUnitTests()/ResetForUnitTests() cycle. That makes the platform's global
// state - Application.Current, Dispatcher.UIThread, the locator root - flicker between "set up"
// and "torn down" thousands of times per run, and anything that observes it from outside the
// session's dispatch thread sees an arbitrary one of those states. Plain [Fact] classes that need
// font resolution did exactly that (see SnapshotService.EnsureAvaloniaInitialized for the four
// distinct failure modes it produced, all order-dependent and all green in isolation).
//
// PerAssembly collapses that to a single steady state on a single thread, which is the invariant
// the whole suite was implicitly assuming. The trade-off is real and accepted: tests no longer get
// a fresh Application, so state they leak on it now outlives them. Nothing in the suite depended
// on the reset - only Infra/SnapshotService.cs so much as reads Application.Current.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

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
