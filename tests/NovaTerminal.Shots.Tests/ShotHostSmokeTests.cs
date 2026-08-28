using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
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
    public async Task ShotHost_WindowBuiltInFirstRunAsyncCanBeCapturedInTheSecond()
    {
        using ShotHost host = ShotHost.Start();

        // Deliberately split across two independent dispatches: build+show in the first,
        // capture+close in the second. This is the shape every future capture scenario
        // needs (build the scene, then render it), and it is the shape that actually
        // breaks under AvaloniaTestIsolationLevel.PerTest: capturing pumps the CURRENT
        // Dispatcher.UIThread and the CURRENT headless render timer (both looked up fresh
        // at capture time), which PerTest rebuilds between calls, disconnecting them from
        // the window's real render pipeline that was set up alongside it in the first call.
        // PerAssembly keeps the same Application/Dispatcher/render timer alive across both
        // calls, so the second call's pump reaches the window's actual pending frame.
        Window window = await host.RunAsync(() =>
        {
            var w = new Window { Width = 320, Height = 200 };
            w.Show();
            return Task.FromResult(w);
        });

        WriteableBitmap? frame = await host.RunAsync(() =>
        {
            WriteableBitmap? captured = window.CaptureRenderedFrame();
            window.Close();
            return Task.FromResult(captured);
        });

        Assert.NotNull(frame);
    }

    [Fact]
    [Trait("Category", "ShotsSmoke")]
    public async Task ShotHost_CanBeDisposedAfterADispatchedBodyThrew()
    {
        using ShotHost host = ShotHost.Start();

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.RunAsync(() => throw new InvalidOperationException("the scenario failed")));

        Assert.Equal("the scenario failed", thrown.Message);

        // The assertion that matters is the `using` above running to completion. The dispatch
        // loop completes its TaskCompletionSource synchronously, so an awaiter resumes on the
        // dispatcher thread; Dispose() there waits on the very task it is running inside. Program
        // reaches Dispose by exactly this route - catch the scenario's exception, finish the run,
        // dispose the host - so before ShotHost yielded on the faulted path too, every failing
        // capture run deadlocked at the end of Main and had to be killed. If that regresses, this
        // test hangs rather than failing, which is the same signal the harness gave.
    }
}
