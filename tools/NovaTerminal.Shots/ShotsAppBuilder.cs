using Avalonia;
using Avalonia.Headless;
using NovaTerminal;

namespace NovaTerminal.Shots;

/// <summary>
/// Entry point handed to <see cref="HeadlessUnitTestSession.StartNew"/>.
///
/// <c>UseHeadlessDrawing = false</c> is the whole point: the headless stub backend accepts
/// every draw call and produces an empty raster, which would make this tool emit blank PNGs
/// that still look like successful captures. This mirrors
/// <c>tests/NovaTerminal.App.Tests/TestAppBuilder.cs</c>.
/// </summary>
public static class ShotsAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false
        });
}
