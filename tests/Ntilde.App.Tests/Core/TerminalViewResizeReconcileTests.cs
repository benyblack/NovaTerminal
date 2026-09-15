using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NovaTerminal.Shell;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// #432: a resize dispatch cancelled before it runs must still reach the buffer and the PTY.
/// </summary>
/// <remarks>
/// <para>
/// <c>OnSizeChanged</c> computes a grid and leaves the 60ms throttle to dispatch it. Anything that
/// stops that timer in between - collapsing the pane, hiding it, detaching it - drops the dispatch,
/// and <c>StartUiTimers</c> deliberately does not restart it. The pane then renders one grid while
/// the buffer and the PTY are on another: wrapping stops matching the pane, and nothing recovers it
/// until some later size happens to differ from the one on record.
/// </para>
/// <para>
/// Recording a dispatch only where it happens is a prerequisite for this and not a cure for it -
/// <c>OnSizeChanged</c> fires on a size <i>change</i>, and a pane that is collapsed and restored
/// comes back at the size it left at, so nothing further arrives to notice. This is the half that
/// re-sends.
/// </para>
/// <para>
/// Driven through a real window rather than a seam, because the bug is an ordering of real events -
/// layout, visibility, timer - and a test that called the pieces directly would be asserting the
/// order it chose itself.
/// </para>
/// </remarks>
public class TerminalViewResizeReconcileTests
{
    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void AResizeCancelledBeforeItDispatches_StillReachesTheBuffer()
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

            // Force the narrower size onto the throttle, so this is about a cancelled dispatch and
            // not about how fast the machine running it is.
            view.HoldResizeThrottleForTest();

            window.Width = 500;
            Dispatcher.UIThread.RunJobs();
            Assert.True(
                view.ResizeDispatchPendingForTest,
                "setup failed: the narrower size should still be waiting on the throttle");

            // The pane goes away before the timer ticks - collapsed, hidden, or detached - which
            // stops the UI timers and takes the pending dispatch with them.
            view.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(
                view.ResizeDispatchPendingForTest,
                "setup failed: hiding the view should have stopped the throttle timer");

            // ...and comes back at the same size it had, which is what a collapse/restore does, so
            // no further size change arrives. Released so the reconciled dispatch goes inline
            // rather than arming a timer the headless harness will never tick; which branch of the
            // throttle runs is not what is under test.
            view.ReleaseResizeThrottleForTest();
            view.IsVisible = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(view.LastDispatchedGridForTest.Cols, buffer.Cols);
            Assert.Equal(view.LastDispatchedGridForTest.Rows, buffer.Rows);
            Assert.Equal(GridColsFor(view), buffer.Cols);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void HidingAndRestoringAPane_WithNothingCancelled_SendsNoResize()
    {
        // The reconcile repairs a cancellation and does nothing else, and the difference
        // matters because the buffer can legitimately be on a grid that TryComputeGrid would
        // not have chosen. The real instance is the font branch of ApplySettings, which
        // derives its grid as Bounds.Width / CellWidth while TryComputeGrid subtracts the
        // padding the draw operation reserves, so the two disagree by a column at some widths.
        // An ungated reconcile would read that standing disagreement as work to do and reflow
        // the buffer, sending SIGWINCH, every time a pane was collapsed and reopened.
        //
        // The disagreement is created here by moving the measured cell size rather than by
        // reproducing the font path at a width where its arithmetic happens to diverge: the
        // gate is about whether a cancellation occurred, not about which calculation is right,
        // so any source of disagreement exercises it and one that is guaranteed to exist
        // exercises it every run.
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);

        int resizes = 0;
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            (int cols, int rows) = view.LastDispatchedGridForTest;
            Assert.True(cols > 0, "nothing was dispatched on show");
            int bufferCols = buffer.Cols;
            int bufferRows = buffer.Rows;

            (double cellWidth, double cellHeight) = view.CellSizeForTest;
            view.SetMetricsForTest((float)cellWidth * 2, (float)cellHeight * 2);

            Assert.True(
                TerminalView.TryComputeGrid(view.Bounds.Size, cellWidth * 2, cellHeight * 2, out int otherCols, out _)
                && otherCols != cols,
                "setup failed: the view and its last dispatch need to disagree for this to assert anything");

            // Counted from here, so only the restore is under test.
            view.OnResize += (_, _) => resizes++;

            // An ordinary hide and restore: nothing was waiting on the throttle, so nothing
            // was cancelled, so there is nothing to repair.
            view.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            view.IsVisible = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, resizes);
            Assert.Equal((cols, rows), view.LastDispatchedGridForTest);
            Assert.Equal(bufferCols, buffer.Cols);
            Assert.Equal(bufferRows, buffer.Rows);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void ACancelledDispatchSupersededWhileHidden_IsNotReconciledOnRestore()
    {
        // A cancelled dispatch is only worth repairing while it is still the last word. Change
        // the font while the pane is hidden and ApplySettings re-grids the buffer and the PTY
        // directly - the dropped resize has been overtaken, and there is nothing to repair.
        // Reconciling anyway would resize away from the grid the font path deliberately chose,
        // which is the same unwanted reflow and SIGWINCH the gate exists to prevent.
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);

        int resizes = 0;
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            (int cols, int rows) = view.LastDispatchedGridForTest;
            Assert.True(cols > 0, "nothing was dispatched on show");

            view.HoldResizeThrottleForTest();
            window.Width = 500;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.ResizeDispatchPendingForTest, "setup failed: nothing was queued");

            view.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(
                view.ResizeDispatchPendingForTest,
                "setup failed: hiding the view should have stopped the throttle timer");

            // The dropped resize is overtaken while the pane is still hidden.
            view.ApplySettings(new TerminalSettings { FontSize = 28 });
            Dispatcher.UIThread.RunJobs();
            (int fontCols, int fontRows) = view.LastDispatchedGridForTest;
            Assert.True(
                (fontCols, fontRows) != (cols, rows),
                "setup failed: the font change dispatched nothing, so nothing superseded the cancelled resize");

            // Force the restore to have something it *could* resize to, so this asserts the
            // gate rather than an accidental agreement between two grid calculations.
            (double cellWidth, double cellHeight) = view.CellSizeForTest;
            view.SetMetricsForTest((float)cellWidth * 2, (float)cellHeight * 2);

            int bufferCols = buffer.Cols;
            int bufferRows = buffer.Rows;
            view.OnResize += (_, _) => resizes++;

            view.ReleaseResizeThrottleForTest();
            view.IsVisible = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, resizes);
            Assert.Equal((fontCols, fontRows), view.LastDispatchedGridForTest);
            Assert.Equal(bufferCols, buffer.Cols);
            Assert.Equal(bufferRows, buffer.Rows);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void AResizeCancelledByDetaching_ReachesTheBufferOnReattach()
    {
        // Detaching is the third way a pending dispatch dies, alongside hiding and collapsing,
        // and it is the one with an ordering hazard: re-attaching can land the view on a top
        // level with a different render scale, and the cell metrics for it are not known until
        // OnAttachedToVisualTree has re-measured. Reconciling before that would compute the
        // grid from the old metrics, which is why it runs after MeasureCharSize rather than
        // from the renderable decision that precedes it.
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);

        var host = new Decorator { Child = view };
        var window = new Window { Content = host, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            (int cols, int rows) = view.LastDispatchedGridForTest;
            Assert.True(cols > 0, "nothing was dispatched on show");
            Assert.Equal(cols, buffer.Cols);

            view.HoldResizeThrottleForTest();
            window.Width = 500;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.ResizeDispatchPendingForTest, "setup failed: nothing was queued");

            host.Child = null;                  // detach: the queued dispatch dies here
            Dispatcher.UIThread.RunJobs();
            Assert.False(
                view.ResizeDispatchPendingForTest,
                "setup failed: detaching should have stopped the throttle timer");

            view.ReleaseResizeThrottleForTest();
            host.Child = view;                  // and back
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(view.LastDispatchedGridForTest.Cols, buffer.Cols);
            Assert.Equal(view.LastDispatchedGridForTest.Rows, buffer.Rows);
            Assert.Equal(GridColsFor(view), buffer.Cols);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The column count the view is actually drawing, independent of its bookkeeping.</summary>
    private static int GridColsFor(TerminalView view)
    {
        (double cellWidth, double cellHeight) = view.CellSizeForTest;
        Assert.True(
            TerminalView.TryComputeGrid(view.Bounds.Size, cellWidth, cellHeight, out int cols, out _),
            $"the view's own bounds {view.Bounds} do not describe a usable grid");
        return cols;
    }
}
