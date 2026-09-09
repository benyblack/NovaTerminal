using System;
using System.Collections.Generic;
using NovaTerminal.VT;
using Xunit;

namespace NovaTerminal.VT.Tests;

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
        var buffer = CreateBufferWithReplacedFrame(out _, out _);
        buffer.BeginSnapshotSession();

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
