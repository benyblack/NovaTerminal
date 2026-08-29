using System;
using System.Collections.Generic;

namespace NovaTerminal.VT.Tests;

/// <summary>
/// #166: images pruned from the buffer must retire their handles into the drain queue so
/// the render layer can dispose them at a frame boundary. Removal from
/// <see cref="TerminalBuffer.Images"/> stays synchronous — only disposal is deferred —
/// so every test here pairs a membership assertion with a drain assertion. Dummy handles
/// stand in for bitmaps: VT never inspects them, and the drain contract is "the exact
/// handles of exactly the removed images, once".
/// </summary>
public class RetiredImageHandleTests
{
    private const int Cols = 20;
    private const int Rows = 5;

    private static (TerminalBuffer buffer, AnsiParser parser) CreateFilledTerminal(int lines = 12)
    {
        var buffer = new TerminalBuffer(Cols, Rows);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < lines; i++)
        {
            parser.Process($"line{i}\r\n");
        }
        return (buffer, parser);
    }

    private static List<object> Drain(TerminalBuffer buffer)
    {
        var drained = new List<object>();
        buffer.DrainRetiredImageHandles(drained, long.MaxValue);
        return drained;
    }

    [Fact]
    public void Ed2_RetiresRemovedViewportImage_NotTheScrollbackOne()
    {
        var (buffer, parser) = CreateFilledTerminal();
        int scrollbackCount = buffer.Scrollback.Count;

        var scrollbackImage = new TerminalImage(new object(), 0, 0, 2, 1);
        var viewportImage = new TerminalImage(new object(), 0, scrollbackCount + 1, 2, 1);
        buffer.AddImage(scrollbackImage);
        buffer.AddImage(viewportImage);

        parser.Process("\x1b[2J");

        Assert.Single(buffer.Images);
        var drained = Drain(buffer);
        Assert.Single(drained);
        Assert.Same(viewportImage.ImageHandle, drained[0]);
        Assert.False(buffer.DrainRetiredImageHandles(new List<object>(), long.MaxValue), "second drain must be empty (no double-retire)");
    }

    [Fact]
    public void Ed3_RetiresRemovedScrollbackImage()
    {
        var (buffer, parser) = CreateFilledTerminal();
        int scrollbackCount = buffer.Scrollback.Count;

        var scrollbackImage = new TerminalImage(new object(), 0, 0, 2, 1);
        var viewportImage = new TerminalImage(new object(), 0, scrollbackCount + 1, 2, 1);
        buffer.AddImage(scrollbackImage);
        buffer.AddImage(viewportImage);

        parser.Process("\x1b[3J");

        Assert.Single(buffer.Images);
        var drained = Drain(buffer);
        Assert.Single(drained);
        Assert.Same(scrollbackImage.ImageHandle, drained[0]);
    }

    [Fact]
    public void DeleteLines_RetiresImageShiftedIntoDeletedRange()
    {
        var (buffer, parser) = CreateFilledTerminal();
        int scrollbackCount = buffer.Scrollback.Count;

        // Image starts inside the region; DL at the top deletes its row.
        var deletedImage = new TerminalImage(new object(), 0, scrollbackCount, 2, 1);
        buffer.AddImage(deletedImage);

        parser.Process("\x1b[1;1H\x1b[1M"); // DL 1 at home

        Assert.Empty(buffer.Images);
        var drained = Drain(buffer);
        Assert.Single(drained);
        Assert.Same(deletedImage.ImageHandle, drained[0]);
    }

    [Fact]
    public void InsertLines_PushingImagePastRegionBottom_RetiresIt()
    {
        var (buffer, parser) = CreateFilledTerminal();
        int scrollbackCount = buffer.Scrollback.Count;

        // Image on the last viewport row; IL at the top pushes it past the region bottom.
        var pushedImage = new TerminalImage(new object(), 0, scrollbackCount + Rows - 1, 2, 1);
        buffer.AddImage(pushedImage);

        parser.Process("\x1b[1;1H\x1b[1L"); // IL 1 at home

        Assert.Empty(buffer.Images);
        var drained = Drain(buffer);
        Assert.Single(drained);
        Assert.Same(pushedImage.ImageHandle, drained[0]);
    }

    [Fact]
    public void Clear_RetiresEveryImage()
    {
        var (buffer, _) = CreateFilledTerminal();
        var first = new TerminalImage(new object(), 0, 0, 2, 1);
        var second = new TerminalImage(new object(), 3, 1, 2, 1);
        buffer.AddImage(first);
        buffer.AddImage(second);

        buffer.Clear();

        Assert.Empty(buffer.Images);
        var drained = Drain(buffer);
        Assert.Equal(2, drained.Count);
        Assert.Contains(first.ImageHandle, drained);
        Assert.Contains(second.ImageHandle, drained);
    }

    [Fact]
    public void ClearImages_RetiresEveryImage()
    {
        var (buffer, _) = CreateFilledTerminal();
        var image = new TerminalImage(new object(), 0, 0, 2, 1);
        buffer.AddImage(image);

        buffer.ClearImages();

        Assert.Empty(buffer.Images);
        var drained = Drain(buffer);
        Assert.Single(drained);
        Assert.Same(image.ImageHandle, drained[0]);
    }

    [Fact]
    public void ImagesThatSurvive_AreNeverRetired()
    {
        var (buffer, parser) = CreateFilledTerminal();
        int scrollbackCount = buffer.Scrollback.Count;

        var survivor = new TerminalImage(new object(), 0, 0, 2, 1); // lives in scrollback
        var victim = new TerminalImage(new object(), 0, scrollbackCount + 1, 2, 1);
        buffer.AddImage(survivor);
        buffer.AddImage(victim);

        parser.Process("\x1b[2J"); // removes only the viewport image

        var drained = Drain(buffer);
        Assert.Single(drained);
        Assert.DoesNotContain(survivor.ImageHandle, drained);
    }

    [Fact]
    public void Drain_WithNothingRetired_ReturnsFalse()
    {
        var (buffer, _) = CreateFilledTerminal();

        Assert.False(buffer.DrainRetiredImageHandles(new List<object>(), long.MaxValue));
    }

    /// <summary>
    /// The drain is age-gated: the owning view passes (now − grace) so a handle a
    /// concurrent snapshot (live frame, agent-host capture) may still be drawing is left
    /// queued until the grace has passed. Retires are FIFO, so the drain stops at the
    /// first too-young entry.
    /// </summary>
    [Fact]
    public void Drain_OnlyRetiresHandlesOlderThanTheCutoff()
    {
        var (buffer, parser) = CreateFilledTerminal();
        int scrollbackCount = buffer.Scrollback.Count;

        // Viewport image: ED 2 actually prunes it (scrollback images survive ED 2).
        var image = new TerminalImage(new object(), 0, scrollbackCount + 1, 2, 1);
        buffer.AddImage(image);

        parser.Process("\x1b[2J");
        Assert.Empty(buffer.Images);

        // Cutoff one minute in the past: the just-retired handle is too young.
        var freshCutoffDrain = new List<object>();
        Assert.False(buffer.DrainRetiredImageHandles(freshCutoffDrain, Environment.TickCount64 - 60_000));
        Assert.Empty(freshCutoffDrain);

        // Long.MaxValue cutoff: everything drains, exactly once.
        var drained = Drain(buffer);
        Assert.Single(drained);
        Assert.Same(image.ImageHandle, drained[0]);
        Assert.False(buffer.DrainRetiredImageHandles(new List<object>(), long.MaxValue));
    }

    /// <summary>
    /// The session gate is the exact rule: a retire entry is only released once no snapshot
    /// session started at or before its retire tick is still active — so a capture that
    /// runs for ANY duration (longer than any grace) still blocks disposal until it ends,
    /// while a session opened after the retire never saw the handle and does not block.
    /// </summary>
    [Fact]
    public void Drain_InFlightSnapshotSession_BlocksDisposalForAnyDuration()
    {
        var (buffer, parser) = CreateFilledTerminal();
        int scrollbackCount = buffer.Scrollback.Count;

        // A capture in flight BEFORE the prune — the exact Greptile P1 scenario.
        int session = buffer.BeginSnapshotSession();

        var image = new TerminalImage(new object(), 0, scrollbackCount + 1, 2, 1);
        buffer.AddImage(image);
        parser.Process("\x1b[2J");
        Assert.Empty(buffer.Images);

        // Age gate fully satisfied (cutoff far in the future) — the session must still block.
        var blocked = new List<object>();
        Assert.False(buffer.DrainRetiredImageHandles(blocked, long.MaxValue));
        Assert.Empty(blocked);

        // Capture ends: the handle is released on the very next drain.
        buffer.EndSnapshotSession(session);
        var released = new List<object>();
        Assert.True(buffer.DrainRetiredImageHandles(released, long.MaxValue));
        Assert.Single(released);
        Assert.Same(image.ImageHandle, released[0]);
    }

    [Fact]
    public void Drain_SessionStartedAfterRetire_DoesNotBlock()
    {
        var (buffer, parser) = CreateFilledTerminal();
        int scrollbackCount = buffer.Scrollback.Count;

        var image = new TerminalImage(new object(), 0, scrollbackCount + 1, 2, 1);
        buffer.AddImage(image);
        parser.Process("\x1b[2J");

        // A session opened after the retire never copied the pruned handle. The sleep steps
        // past the TickCount64 granularity boundary: a session on the SAME tick as the retire
        // conservatively blocks (within one tick we cannot prove the snapshot postdates the
        // prune), so the "does not block" case needs a strictly later tick.
        System.Threading.Thread.Sleep(20);
        int session = buffer.BeginSnapshotSession();
        try
        {
            var drained = new List<object>();
            Assert.True(buffer.DrainRetiredImageHandles(drained, long.MaxValue));
            Assert.Single(drained);
            Assert.Same(image.ImageHandle, drained[0]);
        }
        finally
        {
            buffer.EndSnapshotSession(session);
        }
    }
}
