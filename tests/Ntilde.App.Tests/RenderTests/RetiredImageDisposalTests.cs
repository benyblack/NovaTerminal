using Avalonia.Headless.XUnit;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using SkiaSharp;
using System;

namespace NovaTerminal.Tests.RenderTests;

/// <summary>
/// #166 end to end: a pruned image's SKBitmap is disposed by the owning view's
/// retired-handle drain — but only once the retire-age grace has passed, because a
/// concurrent snapshot (live frame, agent-host capture) taken before the prune may
/// still be drawing it.
/// </summary>
public class RetiredImageDisposalTests
{
    // IsDisposed is protected in SkiaSharp 3.x, and probing a disposed SKBitmap through
    // its public API (GetPixel) dies with a native access violation instead of throwing —
    // so disposal is observed via a subclass flag, never by touching the bitmap.
    private sealed class TrackingBitmap : SKBitmap
    {
        public TrackingBitmap() : base(4, 4) { }
        public bool Gone => IsDisposed;
    }

    [AvaloniaFact]
    public void PrunedImageBitmap_IsDisposedOnlyAfterTheAgeGrace()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);

        var bitmap = new TrackingBitmap();
        buffer.AddImage(new TerminalImage(bitmap, 2, 2, 2, 1));

        // Draining with a cutoff older than the retire must not touch the fresh handle:
        // a snapshot in flight at prune time may still be drawing it.
        view.DisposeRetiredImageBitmaps(Environment.TickCount64 - 60_000);
        Assert.False(bitmap.Gone, "an age-gated drain must leave a freshly-retired bitmap alone");

        // The image is still live in the buffer here — an unpruned image is never disposed.
        Assert.Single(buffer.Images);

        // Prune it (ED 2 semantics): retired with the current tick.
        buffer.ClearScreen();
        Assert.Empty(buffer.Images);
        Assert.False(bitmap.Gone, "pruning alone must not dispose");

        // A drain whose cutoff covers the retire tick disposes it — this is what the
        // view's frame boundary does once the grace has passed.
        view.DisposeRetiredImageBitmaps(Environment.TickCount64);
        Assert.True(bitmap.Gone, "the owning view's drain must dispose the retired bitmap");

        // And nothing re-drains the same handle afterwards.
        view.DisposeRetiredImageBitmaps(Environment.TickCount64);
        Assert.True(bitmap.Gone);
    }

    [AvaloniaFact]
    public void InFlightSnapshotSession_BlocksDisposalForAnyDuration()
    {
        var buffer = new TerminalBuffer(80, 24);
        var view = new TerminalView();
        view.SetBuffer(buffer);

        // A capture in flight before the prune — the scenario the grace alone could not
        // cover: a pass that outlives the age grace must still block disposal until it ends.
        int session = buffer.BeginSnapshotSession();

        var bitmap = new TrackingBitmap();
        buffer.AddImage(new TerminalImage(bitmap, 2, 2, 2, 1));
        buffer.ClearScreen();
        Assert.Empty(buffer.Images);

        // Cutoff far in the future: without the session gate this would dispose mid-capture.
        view.DisposeRetiredImageBitmaps(long.MaxValue);
        Assert.False(bitmap.Gone, "an in-flight snapshot session must block disposal for any duration");

        // The capture completes: the next drain disposes.
        buffer.EndSnapshotSession(session);
        view.DisposeRetiredImageBitmaps(long.MaxValue);
        Assert.True(bitmap.Gone, "disposal must proceed once the blocking session ends");
    }
}
