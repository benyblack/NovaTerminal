using System.Threading;
using Avalonia;
using Xunit;

namespace NovaTerminal.Tests.Infra;

/// <summary>
/// Pins the invariant that the Avalonia headless platform is only ever booted by
/// <c>HeadlessUnitTestSession</c>, on its own dispatch thread — never on an xUnit worker thread
/// by a plain <c>[Fact]</c> that wanted font resolution.
///
/// Booting it constructs a <c>Compositor</c>, which resolves <c>MediaContext.Instance</c> and
/// binds a <em>thread-affine</em> context into whichever locator is current. Do that from a worker
/// thread and it lands in the process-global root, owned by the wrong thread; locator scopes fall
/// through to their parent on a miss, so every later <c>[AvaloniaFact]</c> inherits it and never
/// binds its own, and the first transition or animation it applies throws "The calling thread
/// cannot access this object because a different thread owns it". That was 13 CI failures across
/// four unrelated classes, order-dependent and green in isolation. Three other failure modes come
/// out of the same collision — see <see cref="SnapshotService.EnsureAvaloniaInitialized"/>.
///
/// Deliberately a plain <c>[Fact]</c>, and in the serialized booter collection: under
/// <c>[AvaloniaFact]</c> this would run *on* the dispatch thread, so the owner-thread assertion
/// below would be comparing the session thread against itself and could never fail.
/// </summary>
[Collection("GoldenPng")]
public sealed class AvaloniaBootLocatorHygieneTests
{
    [Fact]
    public void EnsuringAvaloniaIsUp_BootsItOnTheSessionThread_NotTheCallers()
    {
        SnapshotService.EnsureAvaloniaInitialized();

        Assert.NotNull(Application.Current);

        var owner = SnapshotService.AmbientMediaContextOwnerThreadForTest();
        Assert.NotNull(owner);
        Assert.NotSame(Thread.CurrentThread, owner);
    }
}
