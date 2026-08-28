using Avalonia.Headless;

namespace NovaTerminal.Shots;

/// <summary>
/// Owns the headless Avalonia session and the dispatcher thread every scenario runs on.
///
/// The ThreadPool minimums are raised deliberately. Issue #81 traced headless dispatcher
/// deadlocks to PTY loops occupying pool threads while a synchronous wait starved the
/// dispatcher at the default minimum of two. This tool spawns real shells, so it starts
/// from a floor that cannot reproduce that shape.
/// </summary>
public sealed class ShotHost : IDisposable
{
    private readonly HeadlessUnitTestSession _session;

    private ShotHost(HeadlessUnitTestSession session) => _session = session;

    /// <summary>
    /// Starts the one headless Avalonia session this whole process lives on.
    ///
    /// <c>AvaloniaTestIsolationLevel.PerAssembly</c> is passed deliberately.
    /// <c>HeadlessUnitTestSession.StartNew(Type)</c> defaults to
    /// <c>AvaloniaTestIsolationLevel.PerTest</c>, which tears the <c>Application</c> and
    /// <c>Dispatcher</c> down and rebuilds them fresh on every single dispatched call
    /// (decompiled: each dispatch runs <c>EnsureIsolatedApplication</c> -
    /// <c>Dispatcher.ResetBeforeUnitTests</c>, a new <c>AppBuilder.SetupUnsafe</c>, and on
    /// the way out <c>FontManager.Dispose</c> / <c>Dispatcher.ResetForUnitTests</c> / locator
    /// scope exit). That is the right default for xUnit's per-test isolation, but it is the
    /// wrong shape here: a capture scenario is naturally several sequential
    /// <c>RunAsync</c> calls against the same window (build it, then capture it), and
    /// <c>PerTest</c> tears everything down between them, which would hand the second call a
    /// disposed dispatcher instead of the app the first call built. <c>PerAssembly</c> keeps
    /// one <c>Application</c>/<c>Dispatcher</c> alive for the process's whole lifetime
    /// (decompiled: it routes through <c>EnsureSharedApplication</c> instead, which builds
    /// the app once and reuses it), which also means this tool pays app startup cost once
    /// instead of on every dispatched call.
    /// </summary>
    public static ShotHost Start()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 16), Math.Max(completionPorts, 16));

        // Opt out of the ConPTY PSEUDOCONSOLE_PASSTHROUGH spawn path (native/src/lib.rs picks it
        // whenever the host has a real console, which a console app like this one does).
        // Passthrough hands the child the host's own console: the shell's output goes to this
        // process's stdout instead of into the pane - so every capture would be of an empty
        // terminal - and its stdin comes from the host's, which under a redirected or
        // non-interactive parent is at EOF, so bash reads EOF at the first prompt, echoes `exit`
        // and dies before a scenario can send it anything. Both observed on the first end-to-end
        // run. The portable-pty path this selects is the one the GUI app itself always takes.
        Environment.SetEnvironmentVariable("NOVA_PTY_NO_PASSTHROUGH", "1");

        return new ShotHost(HeadlessUnitTestSession.StartNew(
            typeof(ShotsAppBuilder),
            AvaloniaTestIsolationLevel.PerAssembly));
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        try
        {
            return await _session.Dispatch(body, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // HeadlessUnitTestSession's dispatch loop completes its TaskCompletionSource without
            // RunContinuationsAsynchronously, so the awaiter above resumes synchronously, inline,
            // on the dispatcher thread itself. Without this yield, a caller's `using` block would
            // then call Dispose() on that same thread, which blocks waiting for the dispatch loop
            // task to exit - the very task it is currently running inside of. Self-deadlock,
            // confirmed via a hung-process dump: Dispose -> HeadlessUnitTestSession.Dispose ->
            // _dispatchTask.Wait(), stuck under DispatchCore's own TrySetResult continuation on
            // the dispatcher's OS thread. Yielding here forces the rest of the caller onto a
            // thread-pool thread before this method returns.
            //
            // In a finally, not after the await, because a *faulted* dispatch resumes the caller
            // on the dispatcher thread exactly like a successful one does - and that is the path
            // that mattered. Program.cs catches a scenario's exception and carries on to
            // host.Dispose(), so with the yield only on the success path every failing run
            // deadlocked at the end of Main instead of exiting non-zero: the harness had to be
            // killed, which in CI reads as a hang rather than as a red run.
            await Task.Yield();
        }
    }

    public Task RunAsync(Func<Task> body) => RunAsync(async () =>
    {
        await body().ConfigureAwait(true);
        return true;
    });

    public void Dispose() => _session.Dispose();
}
