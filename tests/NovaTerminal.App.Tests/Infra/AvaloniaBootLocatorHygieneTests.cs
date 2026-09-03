using System.Threading;
using Avalonia.Headless.XUnit;
using Xunit;

namespace NovaTerminal.Tests.Infra;

/// <summary>
/// In-process canary for the <c>PlatformBoot</c> lane split.
///
/// <para>
/// <see cref="SnapshotService.EnsureAvaloniaInitialized"/> boots the Avalonia headless platform
/// from a plain <c>[Fact]</c>, which constructs a <c>Compositor</c>, which binds a
/// <em>thread-affine</em> <c>MediaContext</c> into the process-global <c>AvaloniaLocator</c> root
/// — owned by an xUnit worker thread, not <c>HeadlessUnitTestSession</c>'s dispatch thread.
/// Locator scopes fall through to their parent on a miss, so every later <c>[AvaloniaFact]</c> in
/// that process inherits it and throws "The calling thread cannot access this object because a
/// different thread owns it" on the first transition it applies. That was 13 CI failures across
/// four unrelated classes while the job reported success.
/// </para>
///
/// <para>
/// The fix is a process split: every booter carries <c>[Trait("Lane", "PlatformBoot")]</c> and CI
/// runs that lane as its own <c>dotnet test</c> invocation, so no <c>[AvaloniaFact]</c> outside it
/// ever shares a process with a boot. <c>AvaloniaTestSchedulingTests</c> guards the trait at the
/// source level, which is the real protection. This test is the runtime half: it asserts that the
/// <c>MediaContext</c> this process is using belongs to the session's dispatch thread, so a
/// booter that leaked into the main lane shows up as a named failure here instead of as
/// mystery cross-thread throws in whichever animation test happened to run next.
/// </para>
///
/// <para>
/// Honestly a canary, not a proof: it only catches a leak that happened <em>before</em> it runs,
/// and test order is not fixed. It costs nothing and turns a confusing symptom into a clear one.
/// </para>
/// </summary>
public sealed class AvaloniaBootLocatorHygieneTests
{
    [AvaloniaFact]
    public void TheAmbientMediaContextBelongsToTheSessionDispatchThread()
    {
        var owner = SnapshotService.AmbientMediaContextOwnerThreadForTest();

        Assert.NotNull(owner);
        Assert.Same(Thread.CurrentThread, owner);
    }
}
