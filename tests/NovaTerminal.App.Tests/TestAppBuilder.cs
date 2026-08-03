using Avalonia;
using Avalonia.Headless;
using NovaTerminal;

// Global configuration for Avalonia Headless testing
[assembly: AvaloniaTestApplication(typeof(NovaTerminal.Tests.TestAppBuilder))]

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
