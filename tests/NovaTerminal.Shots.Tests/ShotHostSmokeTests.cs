using Avalonia.Controls;
using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class ShotHostSmokeTests
{
    [Fact]
    [Trait("Category", "ShotsSmoke")]
    public async Task ShotHost_RunsAWindowOnTheDispatcherThread()
    {
        using ShotHost host = ShotHost.Start();

        bool shown = await host.RunAsync(() =>
        {
            var window = new Window { Width = 320, Height = 200 };
            window.Show();
            bool visible = window.IsVisible;
            window.Close();
            return Task.FromResult(visible);
        });

        Assert.True(shown, "ShotHost could not show a window; the headless session never started.");
    }

    [Fact]
    [Trait("Category", "ShotsSmoke")]
    public async Task ShotHost_SecondSequentialRunAsyncStillShowsAWindow()
    {
        using ShotHost host = ShotHost.Start();

        bool firstShown = await host.RunAsync(() =>
        {
            var window = new Window { Width = 320, Height = 200 };
            window.Show();
            bool visible = window.IsVisible;
            window.Close();
            return Task.FromResult(visible);
        });

        bool secondShown = await host.RunAsync(() =>
        {
            var window = new Window { Width = 320, Height = 200 };
            window.Show();
            bool visible = window.IsVisible;
            window.Close();
            return Task.FromResult(visible);
        });

        Assert.True(firstShown, "ShotHost could not show a window on the first RunAsync call.");
        Assert.True(secondShown, "ShotHost could not show a window on the second sequential RunAsync call. " +
            "AvaloniaTestIsolationLevel.PerTest would tear the Application/Dispatcher down between calls " +
            "and hand this call a disposed dispatcher instead of a live one.");
    }
}
