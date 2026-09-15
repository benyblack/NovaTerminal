using System;
using System.Collections.Generic;
using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests;

/// <summary>
/// Disposal scheduling for replaced kitty video frames. At 30 fps a fixed two-second
/// disposal grace would retain dozens of full-size bitmaps, so replacements are flagged
/// for immediate disposal at the owning view's next frame boundary — while the
/// snapshot-session gate keeps an in-flight capture of any duration safe.
/// </summary>
public class KittyFrameRetirementTests
{
    private static TerminalBuffer CreateBufferWithReplacedFrame(out object firstHandle, out object secondHandle)
    {
        var buffer = new TerminalBuffer(80, 24);
        firstHandle = new object();
        secondHandle = new object();
        buffer.AddKittyFrame(new TerminalImage(firstHandle, 0, 0, 1, 1), 1);
        buffer.AddKittyFrame(new TerminalImage(secondHandle, 0, 0, 1, 1), 1);
        return buffer;
    }

    [Fact]
    public void ReplacedFrame_IsReleasedImmediately_PastGrace()
    {
        var buffer = CreateBufferWithReplacedFrame(out var firstHandle, out _);

        var drained = new List<object>();
        // retiredBeforeTick far in the past: a grace-gated entry would wait, an immediate
        // one must still release.
        buffer.DrainRetiredImageHandles(drained, Environment.TickCount64 - 60_000);

        Assert.Contains(firstHandle, drained);
    }

    [Fact]
    public void ReplacedFrame_StaysBlockedWhileSnapshotSessionPredatesIt()
    {
        // The session must begin BEFORE the replacement that retires the first frame.
        // This used to call CreateBufferWithReplacedFrame first and open the session
        // after, which inverts the very ordering the test is named for: the gate
        // releases when `entry.Tick < minActiveSessionStart`, and with the session
        // opened second its start tick is >= the retire tick, so the assertion below
        // held ONLY while both Environment.TickCount64 reads landed in the same
        // millisecond. That is a coin flip decided by runner speed - it passed on x64
        // for months and failed on the first ubuntu-24.04-arm release run. Inserting a
        // 2 ms sleep before the old BeginSnapshotSession call reproduced the arm64
        // failure exactly on x64.
        var buffer = new TerminalBuffer(80, 24);
        buffer.AddKittyFrame(new TerminalImage(new object(), 0, 0, 1, 1), 1);

        buffer.BeginSnapshotSession();

        // Sleep, deliberately, and it is not papering over timing: it forces the retire
        // tick STRICTLY past the session start, which is the case the gate actually has
        // to handle. Equal ticks - all the old test ever exercised - are the degenerate
        // one. The assertion is now ordering-determined, so a slower or faster machine
        // cannot change the outcome.
        System.Threading.Thread.Sleep(2);
        buffer.AddKittyFrame(new TerminalImage(new object(), 0, 0, 1, 1), 1);

        var drained = new List<object>();
        buffer.DrainRetiredImageHandles(drained, Environment.TickCount64 - 60_000);

        // The session predates the retire tick, so the safety gate holds even for
        // immediate-disposal entries.
        Assert.Empty(drained);
    }

    [Fact]
    public void ReplacedFrame_ReleasesAfterBlockingSessionEnds()
    {
        var buffer = CreateBufferWithReplacedFrame(out var firstHandle, out _);
        int session = buffer.BeginSnapshotSession();

        buffer.EndSnapshotSession(session);
        var drained = new List<object>();
        buffer.DrainRetiredImageHandles(drained, Environment.TickCount64 - 60_000);

        Assert.Contains(firstHandle, drained);
    }

    [Fact]
    public void ReplacedFrame_LiveImageIsNotRetired()
    {
        var buffer = CreateBufferWithReplacedFrame(out _, out var secondHandle);

        var drained = new List<object>();
        buffer.DrainRetiredImageHandles(drained, Environment.TickCount64);

        Assert.DoesNotContain(secondHandle, drained);
        Assert.Single(buffer.Images);
    }
}
