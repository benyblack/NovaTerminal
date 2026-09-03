using System.Threading;
using Avalonia.Headless;
using Xunit;

// Runs once, before any test in the assembly. See AssemblyHeadlessSessionWarmup.
[assembly: AssemblyFixture(typeof(NovaTerminal.Tests.Infra.AssemblyHeadlessSessionWarmup))]

namespace NovaTerminal.Tests.Infra;

/// <summary>
/// Starts <c>HeadlessUnitTestSession</c> and drives one no-op through it before any test in this
/// assembly runs, so Avalonia's thread-affine global state is established on the session's
/// dispatch thread and nothing else can claim it first.
/// </summary>
/// <remarks>
/// <para>
/// Two Avalonia globals are first-touch-wins. <c>Dispatcher.UIThread</c> creates its instance
/// lazily and keeps whichever thread created it (<c>s_uiThread ??= this</c>, and
/// <c>CheckAccess()</c> is a bare <c>Thread.CurrentThread == _thread</c>).
/// <c>MediaContext.Instance</c> then captures that dispatcher and binds itself into the
/// process-global <c>AvaloniaLocator</c>, whose child scopes fall through to the parent on a
/// lookup miss. Whoever gets there first therefore decides, for the whole process, which thread
/// is allowed to touch the render and animation machinery.
/// </para>
/// <para>
/// Under Avalonia's default <c>PerTest</c> isolation that was survivable: every
/// <c>[AvaloniaFact]</c> ran <c>Dispatcher.ResetBeforeUnitTests()</c> and re-established the
/// globals in a fresh locator scope, healing the damage each time. This assembly runs
/// <c>PerAssembly</c> instead (see <c>TestAppBuilder</c>), which sets up once and never resets —
/// so a plain <c>[Fact]</c> that touched <c>Dispatcher.UIThread</c>, or booted the platform,
/// before the session got going would pin the wrong thread permanently, and every later
/// <c>[AvaloniaFact]</c> would throw "The calling thread cannot access this object because a
/// different thread owns it" on the first transition it applied. Getting that answered the same
/// way on every run is the point: order-dependence is what made the original bug present as a
/// different set of failures each time, and never in isolation.
/// </para>
/// <para>
/// An xUnit assembly fixture, not a <c>[ModuleInitializer]</c>. Module initializers run under the
/// assembly loader lock, and the blocking cross-thread dispatch below needs the session thread to
/// load Avalonia types — which needs that same lock. That deadlocks, and it deadlocks during
/// xUnit's assembly-info probe rather than in a test, so it surfaces as
/// "Catastrophic failure: Test process did not respond within 60 seconds" with no tests run at
/// all.
/// </para>
/// </remarks>
public sealed class AssemblyHeadlessSessionWarmup
{
    private static readonly object Gate = new();
    private static bool s_done;

    public AssemblyHeadlessSessionWarmup() => Ensure();

    internal static void Ensure()
    {
        if (s_done) return;

        lock (Gate)
        {
            if (s_done) return;

            HeadlessUnitTestSession
                .GetOrStartForAssembly(typeof(AssemblyHeadlessSessionWarmup).Assembly)
                .Dispatch(static () => { }, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            s_done = true;
        }
    }
}
