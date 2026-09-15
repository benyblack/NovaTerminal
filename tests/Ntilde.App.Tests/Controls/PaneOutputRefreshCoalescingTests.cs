using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Xunit;

namespace Ntilde.Tests.Controls;

/// <summary>
/// The after-output UI refresh is queued once per chunk read from the session, on the PTY/SSH read
/// thread. Posting one dispatcher job per chunk makes the queue grow with the <em>remote's</em> send
/// rate rather than the UI's drain rate, and a busy SSH session outruns the UI thread easily — each
/// queued closure doing the same work as the one in front of it, plus the second job
/// <c>UpdateScrollUI</c> posts of its own.
///
/// These pin the coalescing that replaced it. Everything the job does is a level rather than an
/// edge — the scrollbar reads the buffer's current extent, the window stamps "output seen at" — so
/// collapsing a burst loses nothing, provided one pass still runs after the last chunk.
/// </summary>
public class PaneOutputRefreshCoalescingTests
{
    private static void RequestRefresh(TerminalPane pane)
    {
        MethodInfo queue = typeof(TerminalPane).GetMethod(
            "QueueOutputUiRefresh",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        queue.Invoke(pane, null);
    }

    [AvaloniaFact]
    public void ABurstOfChunks_CollapsesToASingleRefresh()
    {
        using var pane = new TerminalPane();
        int refreshes = 0;
        pane.OutputReceived += _ => refreshes++;

        // 500 chunks arriving before the UI thread gets a turn — the shape of `cat` of a large
        // file over SSH.
        for (int i = 0; i < 500; i++)
        {
            RequestRefresh(pane);
        }

        Assert.Equal(0, refreshes); // nothing runs until the dispatcher does
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, refreshes);
    }

    [AvaloniaFact]
    public void TheNextBurst_RefreshesAgain()
    {
        // The guard against the opposite failure: a coalescing flag that is set and never cleared
        // collapses every future burst too, and the pane stops updating until something else
        // happens to refresh it.
        using var pane = new TerminalPane();
        int refreshes = 0;
        pane.OutputReceived += _ => refreshes++;

        for (int burst = 1; burst <= 5; burst++)
        {
            for (int i = 0; i < 50; i++)
            {
                RequestRefresh(pane);
            }

            Dispatcher.UIThread.RunJobs();
            Assert.Equal(burst, refreshes);
        }
    }

    [AvaloniaFact]
    public void OutputArrivingDuringTheRefresh_QueuesAnotherOne()
    {
        // Why the flag is cleared at the start of the job rather than the end. A chunk that lands
        // mid-pass has not been reflected by the read that pass already did, so it has to be able
        // to queue the next one. Clearing at the end would swallow it, and the newest output would
        // sit unshown until an unrelated refresh came along.
        using var pane = new TerminalPane();
        int refreshes = 0;
        bool reentered = false;

        pane.OutputReceived += _ =>
        {
            refreshes++;
            if (!reentered)
            {
                reentered = true;
                RequestRefresh(pane); // a chunk arriving while this pass runs
            }
        };

        RequestRefresh(pane);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, refreshes);
    }

    [AvaloniaFact]
    public void ARefreshQueuedBeforeDisposal_DoesNotRunAfterIt()
    {
        var pane = new TerminalPane();
        int refreshes = 0;
        pane.OutputReceived += _ => refreshes++;

        RequestRefresh(pane);
        pane.Dispose();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, refreshes);
    }
}
