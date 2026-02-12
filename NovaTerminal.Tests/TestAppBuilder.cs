using Avalonia;
using Avalonia.Headless;
using NovaTerminal;

// Global configuration for Avalonia Headless testing
[assembly: AvaloniaTestApplication(typeof(NovaTerminal.Tests.TestAppBuilder))]

namespace NovaTerminal.Tests
{
    public class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
