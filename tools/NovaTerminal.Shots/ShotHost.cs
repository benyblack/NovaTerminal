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

    public static ShotHost Start()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 16), Math.Max(completionPorts, 16));

        return new ShotHost(HeadlessUnitTestSession.StartNew(typeof(ShotsAppBuilder)));
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        T result = await _session.Dispatch(body, CancellationToken.None).ConfigureAwait(false);

        // HeadlessUnitTestSession's dispatch loop completes its TaskCompletionSource without
        // RunContinuationsAsynchronously, so the awaiter above resumes synchronously, inline,
        // on the dispatcher thread itself. Without this yield, a caller's `using` block would
        // then call Dispose() on that same thread, which blocks waiting for the dispatch loop
        // task to exit - the very task it is currently running inside of. Self-deadlock,
        // confirmed via a hung-process dump: Dispose -> HeadlessUnitTestSession.Dispose ->
        // _dispatchTask.Wait(), stuck under DispatchCore's own TrySetResult continuation on
        // the dispatcher's OS thread. Yielding here forces the rest of the caller onto a
        // thread-pool thread before this method returns.
        await Task.Yield();
        return result;
    }

    public Task RunAsync(Func<Task> body) => RunAsync(async () =>
    {
        await body().ConfigureAwait(true);
        return true;
    });

    public void Dispose() => _session.Dispose();
}
