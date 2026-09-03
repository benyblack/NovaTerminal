using System;
using System.Collections.Generic;
using System.IO;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Source-scan guards over how the test suite is allowed to touch Avalonia's process-global,
/// thread-affine platform state.
///
/// <para>
/// Two globals are first-touch-wins. <c>Dispatcher.UIThread</c> creates its instance lazily and
/// keeps whichever thread created it, and <c>CheckAccess()</c> is a bare
/// <c>Thread.CurrentThread == _thread</c>. <c>MediaContext.Instance</c> then captures that
/// dispatcher and binds itself into the process-global <c>AvaloniaLocator</c>, whose child scopes
/// fall through to the parent on a lookup miss. So whichever thread boots the platform decides,
/// for the whole process, who may touch the render and animation machinery.
/// </para>
///
/// <para>
/// #317 saw one face of this and read it as a race, mitigating it by serializing the booters into
/// one non-parallel collection. That was the wrong diagnosis: booting the platform is a
/// <em>lasting</em> global side effect, so serialization changes nothing and only test
/// <em>order</em> ever mattered. The rule that actually fixes it is the one
/// <see cref="NothingInTheTestSuiteBootsAvaloniaDirectly"/> enforces — the platform is booted
/// exactly once, by <c>HeadlessUnitTestSession</c> on its own dispatch thread, driven from
/// <c>AssemblyHeadlessSessionWarmup</c> before any test runs.
/// </para>
///
/// <para>
/// The collection rule below is kept because it is still true of the render path those classes
/// share, but it is no longer what protects platform ownership. A source scan rather than a
/// runtime check in both cases, because by the time the damage shows up it is a confusing
/// failure in an unrelated test.
/// </para>
/// </summary>
public class AvaloniaTestSchedulingTests
{
    // A call site, not the bare identifier: this file names the method in its own prose,
    // and a guard that flags itself is worse than no guard.
    private const string Booter = "SnapshotService.EnsureAvaloniaInitialized(";
    private const string RequiredCollection = "[Collection(\"GoldenPng\")]";

    /// <summary>
    /// Ways to boot the Avalonia platform. <c>SetupWithoutStarting</c> was the original offender
    /// (in <c>SnapshotService</c>); the others are the neighbouring doors into the same global
    /// state, listed so a future "just call Setup instead" cannot walk around the guard.
    /// </summary>
    private static readonly string[] PlatformBootCalls =
    [
        ".SetupWithoutStarting(",
        ".SetupUnsafe(",
        ".SetupWithLifetime(",
        ".StartWithClassicDesktopLifetime(",
    ];

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NovaTerminal.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    /// <summary>Test sources, excluding build output, which can carry copies under some SDKs.</summary>
    private static IEnumerable<(string Relative, string Text)> TestSources()
    {
        string root = RepoRoot();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (relative, File.ReadAllText(file));
        }
    }

    /// <summary>
    /// The platform must be booted only by <c>HeadlessUnitTestSession</c>, on its dispatch thread.
    /// </summary>
    /// <remarks>
    /// <c>SnapshotService.EnsureAvaloniaInitialized</c> used to call
    /// <c>BuildAvaloniaApp().SetupWithoutStarting()</c> itself. Its callers are plain
    /// <c>[Fact]</c> bodies and constructors, so that booted the platform on whichever thread
    /// xUnit picked, and the collision with the session's own initialisation had four faces, all
    /// order-dependent and all green in isolation: a thread-affine <c>MediaContext</c> left in
    /// the locator root, which every later <c>[AvaloniaFact]</c> inherited and threw on
    /// (13 CI failures across four unrelated classes, plus three downstream layout assertions);
    /// "Setup was already called on one of AppBuilder instances" when an <c>[AvaloniaFact]</c>
    /// went first; a cross-thread throw inside the boot itself; and hangs when the
    /// <c>MediaContext</c> and <c>Compositor</c> ended up owned by different threads.
    /// </remarks>
    [Fact]
    public void NothingInTheTestSuiteBootsAvaloniaDirectly()
    {
        var offenders = new List<string>();

        foreach ((string relative, string text) in TestSources())
        {
            // This guard names the calls in its own PlatformBootCalls table and prose.
            if (Path.GetFileName(relative) == "AvaloniaTestSchedulingTests.cs")
            {
                continue;
            }

            foreach (string call in PlatformBootCalls)
            {
                if (text.Contains(call, StringComparison.Ordinal))
                {
                    offenders.Add($"{relative} ({call.Trim('.', '(')})");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These test files boot the Avalonia platform directly. The platform is thread-affine "
            + "and booting it is a permanent global side effect, so it must be left to "
            + "HeadlessUnitTestSession on its own dispatch thread — which "
            + "AssemblyHeadlessSessionWarmup already arranges before any test runs. Depend on "
            + "that instead (SnapshotService.EnsureAvaloniaInitialized is the supported entry "
            + "point): " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryTestThatBootsAvaloniaIsInTheSerializedCollection()
    {
        var offenders = new List<string>();

        foreach ((string relative, string text) in TestSources())
        {
            if (!text.Contains(Booter, StringComparison.Ordinal))
            {
                continue;
            }

            // Two files mention the call without making it: the helper that declares it, and
            // this guard, whose needle and prose both contain it.
            string name = Path.GetFileName(relative);
            if (name is "SnapshotService.cs" or "AvaloniaTestSchedulingTests.cs")
            {
                continue;
            }

            if (!text.Contains(RequiredCollection, StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These test files render through the shared snapshot path but are not in the "
            + "serialized \"GoldenPng\" collection, so they can run alongside each other (#317): "
            + string.Join(", ", offenders));
    }
}
