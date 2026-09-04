using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// Source-scan guards over how the test suite is allowed to touch Avalonia's process-global,
/// thread-affine platform state.
///
/// <para>
/// Booting the headless platform constructs a <c>Compositor</c>, which resolves
/// <c>MediaContext.Instance</c>. That lazily binds a thread-affine <c>MediaContext</c> — its
/// <c>Dispatcher.CheckAccess()</c> is a bare <c>Thread.CurrentThread == _thread</c> — into
/// <c>AvaloniaLocator.CurrentMutable</c>, and locator child scopes fall through to their parent on
/// a lookup miss. Boot from a plain <c>[Fact]</c>, which runs on an arbitrary xUnit thread outside
/// the per-test scope, and the binding lands in the process root owned by the wrong thread; every
/// later <c>[AvaloniaFact]</c> then inherits it, never binds its own, and throws on the first
/// transition it applies.
/// </para>
///
/// <para>
/// #317 saw one face of this and read it as a race, mitigating it by serializing the booters into
/// one non-parallel collection. That was the wrong diagnosis: the boot is a <em>lasting</em>
/// global side effect, so serialization changes nothing and only test <em>order</em> ever
/// mattered — which is why that guard stayed green through 13 CI failures across four unrelated
/// classes. Nor is it fixable in-process: deleting the stray root binding after the boot trades
/// the failures for a hang in <c>VerticalTabStripTests</c>, and assembly-wide isolation
/// (<c>PerAssembly</c>), which would let the booters borrow a session-owned application, deadlocks
/// every plain <c>[Fact]</c> that marshals onto <c>Dispatcher.UIThread</c>. Both were tried.
/// </para>
///
/// <para>
/// What does work is not sharing the process: every booter carries
/// <c>[Trait("Lane", "PlatformBoot")]</c> and CI runs that lane as its own <c>dotnet test</c>
/// invocation. The rules below enforce that exactly one place boots the platform and that every
/// test reaching it is in the lane. The GoldenPng collection rule stays too, because those
/// classes still share the snapshot render path. Source scans rather than runtime checks, because
/// by the time the damage shows up it is a confusing failure in an unrelated test.
/// </para>
/// </summary>
public class AvaloniaTestSchedulingTests
{
    // A call site, not the bare identifier: this file names the method in its own prose,
    // and a guard that flags itself is worse than no guard.
    private const string Booter = "SnapshotService.EnsureAvaloniaInitialized(";
    private const string RequiredCollection = "[Collection(\"GoldenPng\")]";

    /// <summary>
    /// Entry points that boot the platform on the caller's behalf, by calling
    /// <c>EnsureAvaloniaInitialized</c> internally.
    /// </summary>
    /// <remarks>
    /// Checking only the direct call was very nearly a no-op: exactly one test file in the
    /// repository named it, so this guard was verifying a single file while every other
    /// snapshot-path renderer reached the same global state through <c>CapturePng</c> and was
    /// skipped. <c>BoxDrawingRenderScalingTests</c> came in that way (#346), sat in the main
    /// lane with neither the trait nor the collection, and turned the Unit Tests job red on both
    /// OSes - 17 failures across two unrelated classes - for the week it took to find. A guard
    /// that looks for the polite spelling of a hazard catches only polite hazards.
    /// </remarks>
    private static readonly string[] TransitiveBooters =
    [
        "SnapshotService.Capture(",
        "SnapshotService.CapturePng(",
    ];

    /// <summary>
    /// Categories CI runs as their own <c>dotnet test</c> invocation. A file carrying one of
    /// these is already in a process of its own, which is the same containment the PlatformBoot
    /// lane provides - so it satisfies the invariant without the trait.
    /// </summary>
    /// <remarks>
    /// Kept in step with the <c>--filter</c> arguments in <c>.github/workflows/ci.yml</c>: the
    /// headless App.Tests step excludes each of these, and a separate job runs it. If a category
    /// is dropped from that exclusion list, it stops isolating and belongs out of this table.
    /// </remarks>
    private static readonly string[] IsolatingCategories =
    [
        "RenderMetrics",
        "GoldenSharedPng",
        "GoldenFontPng",
        "Replay",
        "Stress",
        "PtySmoke",
    ];

    /// <summary>True when the file boots the platform, directly or through a helper that does.</summary>
    private static bool Boots(string text) =>
        text.Contains(Booter, StringComparison.Ordinal)
        || TransitiveBooters.Any(call => text.Contains(call, StringComparison.Ordinal));

    /// <summary>True when a category already gives the file its own CI process.</summary>
    private static bool IsolatedByCategory(string text) =>
        IsolatingCategories.Any(category =>
            text.Contains($"[Trait(\"Category\", \"{category}\")]", StringComparison.Ordinal));

    /// <summary>The single file allowed to boot the platform.</summary>
    private const string BootOwner = "SnapshotService.cs";

    /// <summary>
    /// The lane every booter must sit in. CI runs it as its own `dotnet test` invocation, so a
    /// boot never shares a process with an [AvaloniaFact] that is not in the lane.
    /// </summary>
    private const string RequiredLane = "[Trait(\"Lane\", \"PlatformBoot\")]";

    /// <summary>
    /// Ways to boot the Avalonia platform. <c>SetupWithoutStarting</c> is the one
    /// <c>SnapshotService</c> uses; the others are the neighbouring doors into the same global
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
    /// One place boots the platform, so there is one place that documents the lane requirement
    /// and one place to change if Avalonia ever makes this safe. A second booter elsewhere would
    /// escape the lane guard below.
    /// </summary>
    [Fact]
    public void OnlySnapshotServiceBootsAvalonia()
    {
        var offenders = new List<string>();
        var owner = default((string Relative, string Text)?);

        foreach ((string relative, string text) in TestSources())
        {
            string name = Path.GetFileName(relative);

            // This guard names the calls in its own PlatformBootCalls table and prose.
            if (name == "AvaloniaTestSchedulingTests.cs")
            {
                continue;
            }

            bool boots = false;
            foreach (string call in PlatformBootCalls)
            {
                if (text.Contains(call, StringComparison.Ordinal))
                {
                    boots = true;
                    if (name != BootOwner)
                    {
                        offenders.Add($"{relative} ({call.Trim('.', '(')})");
                    }
                }
            }

            if (boots && name == BootOwner)
            {
                owner = (relative, text);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Only {BootOwner} may boot the Avalonia platform. It is thread-affine and booting it "
            + "leaves a MediaContext bound in the process-global locator root, which every later "
            + "[AvaloniaFact] inherits and throws on. Call "
            + "SnapshotService.EnsureAvaloniaInitialized() instead, and put the test in the "
            + "PlatformBoot lane. Offenders: " + string.Join(", ", offenders));

        Assert.True(
            owner is not null,
            $"No file boots the Avalonia platform any more. If that move was deliberate, this "
            + $"guard and the PlatformBoot lane it enforces need rewriting rather than deleting — "
            + $"plain [Fact] tests still need a font manager from somewhere.");

    }

    /// <summary>
    /// Every test that boots the platform must be in the PlatformBoot lane, which CI runs in its
    /// own process, and in the serialized GoldenPng collection.
    /// </summary>
    /// <remarks>
    /// The lane is the load-bearing half: booting leaves a thread-affine MediaContext in the
    /// process-global locator root, so the only reliable containment is not sharing the process
    /// with the [AvaloniaFact] tests that would inherit it. The collection remains because these
    /// classes also share the snapshot render path (#317).
    /// </remarks>
    [Fact]
    public void EveryTestThatBootsAvaloniaIsInThePlatformBootLaneAndSerializedCollection()
    {
        var missingLane = new List<string>();
        var missingCollection = new List<string>();

        foreach ((string relative, string text) in TestSources())
        {
            if (!Boots(text))
            {
                continue;
            }

            // Two files mention the calls without making them: the helper that declares them, and
            // this guard, whose needles and prose both contain them.
            string name = Path.GetFileName(relative);
            if (name is "SnapshotService.cs" or "AvaloniaTestSchedulingTests.cs")
            {
                continue;
            }

            // Either containment will do, because what matters is not sharing the process with an
            // [AvaloniaFact] that would inherit the MediaContext - the lane achieves that by
            // trait, an isolating category by having its own CI invocation.
            if (!text.Contains(RequiredLane, StringComparison.Ordinal) && !IsolatedByCategory(text))
            {
                missingLane.Add(relative);
            }

            // The collection is only meaningful inside the shared lane: the isolating categories
            // each run alone, so there is nothing there for it to serialize against. Requiring it
            // of them would be noise, and widening it is a separate decision from this fix.
            if (text.Contains(RequiredLane, StringComparison.Ordinal)
                && !text.Contains(RequiredCollection, StringComparison.Ordinal))
            {
                missingCollection.Add(relative);
            }
        }

        Assert.True(
            missingLane.Count == 0,
            "These test files boot the Avalonia headless platform but are not in the PlatformBoot "
            + "lane, so they share a process with [AvaloniaFact] tests. Booting binds a "
            + "thread-affine MediaContext into the process-global AvaloniaLocator root, which "
            + "every later [AvaloniaFact] inherits and throws on. Add "
            + "[Trait(\"Lane\", \"PlatformBoot\")] (and check ci.yml runs that lane), or give the "
            + "file a category CI runs as its own invocation: "
            + string.Join(", ", missingLane));

        Assert.True(
            missingCollection.Count == 0,
            "These test files render through the shared snapshot path but are not in the "
            + "serialized \"GoldenPng\" collection, so they can run alongside each other (#317): "
            + string.Join(", ", missingCollection));
    }
}
