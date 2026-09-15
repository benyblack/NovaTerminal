using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NovaTerminal.Shell;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// The resize tracking must describe what the buffer and the PTY were actually told, not what a
/// layout pass happened to compute.
/// </summary>
/// <remarks>
/// <para>
/// <c>OnSizeChanged</c> computes a grid and leaves the 60ms throttle to dispatch it, so between
/// those two moments the grid is decided but unsent. Recording it as sent in the first moment
/// makes the field a record of intent rather than of fact, and a dispatch that never runs is then
/// indistinguishable from one that did - which is the ground the #432 desync stands on.
/// </para>
/// <para>
/// Driven through a real window rather than a seam, because these are properties of the real
/// layout path; a test that called the pieces directly would be asserting the order it chose
/// itself.
/// </para>
/// </remarks>
public class TerminalViewResizeTrackingTests
{
    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void AResizeWaitingOnTheThrottle_IsNotYetRecordedAsDispatched()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);

        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.Bounds.Width > 0, $"the view was never arranged: {view.Bounds}");

            (int cols, int rows) = view.LastDispatchedGridForTest;
            Assert.True(cols > 0 && rows > 0, $"nothing was dispatched on show: {cols}x{rows}");
            Assert.Equal(cols, buffer.Cols);

            // Force the next change onto the throttle rather than the inline path.
            view.HoldResizeThrottleForTest();

            window.Width = 500;
            Dispatcher.UIThread.RunJobs();
            Assert.True(
                view.ResizeDispatchPendingForTest,
                "setup failed: the narrower size should still be waiting on the throttle");

            // Nothing has been dispatched, so nothing may be recorded as dispatched - and the
            // record must still agree with the grid the buffer is genuinely on.
            Assert.Equal((cols, rows), view.LastDispatchedGridForTest);
            Assert.Equal(cols, buffer.Cols);
            Assert.Equal(rows, buffer.Rows);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void AFontChangeThatRegridsTheBuffer_IsRecordedAsDispatched()
    {
        // The third dispatch site, and the one easiest to miss: a font change re-grids the
        // buffer and the PTY straight from the new metrics, bypassing the throttle. It has to
        // record what it sent like the other two, or the field goes stale the moment anyone
        // changes their font size - and every later comparison against it is then wrong.
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);

        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            (int cols, int rows) = view.LastDispatchedGridForTest;
            Assert.True(cols > 0, "nothing was dispatched on show");

            var settings = new TerminalSettings { FontSize = 28 };
            view.ApplySettings(settings);
            Dispatcher.UIThread.RunJobs();

            Assert.True(
                buffer.Cols != cols || buffer.Rows != rows,
                $"setup failed: the larger font did not re-grid the buffer, still {buffer.Cols}x{buffer.Rows}");

            // Whatever it dispatched, the record names it - so it still describes the grid the
            // buffer is genuinely on.
            Assert.Equal((buffer.Cols, buffer.Rows), view.LastDispatchedGridForTest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void AQueuedDispatchSendsTheSizeTheViewEndedAt_NotTheOneThatArmedIt()
    {
        // Comparing against the last *dispatched* grid means a view that leaves a size and comes
        // back to it inside one throttle window reports no change on the way back. The queued
        // dispatch still has to name where the view actually ended up, or the throttle would fire
        // the intermediate size at a view that has already left it. That is why the pending grid
        // is recorded on every pass, not only on a change.
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);

        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            (int cols, int rows) = view.LastDispatchedGridForTest;
            Assert.True(cols > 0, "nothing was dispatched on show");

            view.HoldResizeThrottleForTest();

            window.Width = 500;                 // away from the dispatched size: arms the throttle
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.ResizeDispatchPendingForTest, "setup failed: nothing was queued");
            Assert.NotEqual(cols, view.PendingGridForTest.Cols);

            window.Width = 800;                 // and back, still inside the same window
            Dispatcher.UIThread.RunJobs();

            Assert.Equal((cols, rows), view.PendingGridForTest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
