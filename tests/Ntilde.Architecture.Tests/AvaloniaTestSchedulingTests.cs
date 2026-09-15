using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

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
    /// This must mirror the <c>Category!=</c> exclusions on the headless App.Tests step in
    /// <c>.github/workflows/ci.yml</c> exactly, and
    /// <see cref="EveryIsolatingCategoryIsActuallyExcludedInCi"/> asserts that it does. A
    /// hand-kept mirror is how this went wrong once already: <c>GoldenFontPng</c> sat in this
    /// table on the assumption that a category named after a golden-PNG job must have one, when
    /// the string appears nowhere in <c>ci.yml</c> - so it excluded nothing, and a test relying on
    /// it alone would have passed the guard while still sharing the process.
    /// <c>GoldenFontPngTests</c> was unaffected because it also carries the lane trait.
    /// </remarks>
    private static readonly string[] IsolatingCategories =
    [
        "RenderMetrics",
        "GoldenSharedPng",
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

    /// <summary>
    /// No <c>static</c> field or property initializer in <c>src/</c> may construct a thread-affine
    /// Avalonia drawing object. They are <c>AvaloniaObject</c>s, so every property read goes
    /// through <c>VerifyAccess</c>: one cached in a static is created once per process, owned by
    /// whichever thread first touched the type, and every later read from another thread throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The headless lane makes that certain rather than unlikely. Under <c>PerTest</c> isolation
    /// Avalonia nulls <c>Dispatcher.s_uiThread</c> at every test boundary and the next test rebinds
    /// it to whichever threadpool worker serves it - measured at twenty-one migrations across eight
    /// workers in one run, by reading the private field directly, since the
    /// <c>Dispatcher.UIThread</c> property heals the very state a probe is trying to observe. An
    /// object created under one binding and rendered under another throws
    /// <c>"The calling thread cannot access this object because a different thread owns it"</c>
    /// inside <c>MediaContext.Render</c>, and whichever test happens to be pumping the dispatcher
    /// wears a failure it did not cause.
    /// </para>
    /// <para>
    /// Not hypothetical: six such brushes on <c>MainWindow</c> - the tab dots and marker chips -
    /// took out eighteen <c>VerticalTabStripTests</c> and <c>TabRunningCommandTests</c> in two of
    /// five runs on main, in a cascade read as flakiness for months.
    /// <c>VerticalTabStripTests.DotColorOf</c> reads <c>SolidColorBrush.get_Color</c>, which is the
    /// throwing frame. It is a production hazard too, not only a test one: any static
    /// <c>AvaloniaObject</c> touched off the UI thread has the same defect.
    /// </para>
    /// <para>
    /// <strong>Source, not reflection, and deliberately so.</strong> Reading the fields answers the
    /// question exactly - it sees through arrays, ternaries and auto-property backing fields
    /// without caring how the initializer was written - and a draft of this guard did that. It also
    /// runs every declaring type's static initializer, and one of those, <c>AppLogger</c>, truncates
    /// the real <c>debug.log</c> under <c>%LOCALAPPDATA%</c>. A guard that damages the developer's
    /// machine to check a rule is not worth the precision, so this reads the text instead.
    /// </para>
    /// <para>
    /// Two gaps are deliberate, both narrow and both preferred to their alternative. A brush that
    /// a <c>Lazy&lt;IBrush&gt;</c> caches behind a lambda is not reported, because the rule that
    /// spares a <c>Func&lt;IBrush&gt;</c> factory - nothing past a lambda arrow runs at type-init -
    /// cannot tell the two apart from the text, and wrongly failing correct code is the worse of
    /// the two errors. Nor is a target-typed <c>new()</c> buried inside a ternary, where neither
    /// the initializer's first token nor the constructor names a type. Each further spelling costs
    /// more false-positive risk than it removes hazard; the common shape - and all sixteen real
    /// ones - is a plain static field assigned a constructor, and that is caught every way it can
    /// be written.
    /// </para>
    /// <para>
    /// What it does catch, all verified against a probe: wrapped initializers, fully qualified and
    /// <c>global::</c> constructors, target-typed <c>new()</c>, auto-properties, and affine objects
    /// nested anywhere inside the initializer - inside an array, a ternary, a collection
    /// initializer or a lambda. What it cannot see is a brush built behind a factory method, since
    /// the initializer no longer names the type. Reviews found the first two drafts of this guard
    /// short exactly one spelling each, which is the failure the lane rule below already warns
    /// about, so the limit is stated rather than left to be discovered.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoStaticInitializerConstructsAThreadAffineDrawingObject()
    {
        var offenders = new List<string>();
        string root = RepoRoot();

        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = WithoutCommentsAndLiterals(File.ReadAllText(file));
            foreach ((int index, string declaration, string initializer) in StaticInitializers(text))
            {
                int line = text.Take(index).Count(c => c == (char)10) + 1;
                string where = $"{Path.GetRelativePath(root, file)}:{line}";

                foreach (Match construction in AffineConstruction.Matches(initializer))
                {
                    // Anything past a lambda arrow runs per call, not once at type-init, so a
                    // cached factory - static Func<IBrush> Make = () => new SolidColorBrush(...) -
                    // is exactly the "build a fresh instance per use" the message recommends and
                    // must not be reported for following the advice (local codex review).
                    if (initializer[..construction.Index].Contains("=>", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    offenders.Add($"{where}  {construction.Value}");
                }

                // The target-typed form names no type on the right, so the type on the left is the
                // only place the hazard is written down.
                if (AffineTypeName.IsMatch(declaration) && TargetTypedNew.IsMatch(initializer))
                {
                    offenders.Add($"{where}  {declaration.Trim()} new(...)");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These static initializers construct thread-affine Avalonia drawing objects. A static is "
            + "created once per process, owned by the thread that first touched the type, and every "
            + "read from another thread throws \"a different thread owns it\" - in the headless "
            + "lane, as a render failure inside an unrelated test. Use an immutable equivalent "
            + "(ImmutableSolidColorBrush, ImmutablePen, ...), which has no property system and no "
            + "thread affinity, or build a fresh instance per use: "
            + string.Join("; ", offenders));
    }

    /// <summary>
    /// The initializer text of every <c>static</c> field or property, with its offset. Both shapes
    /// are matched up to the <c>=</c>; the value that follows is taken by balancing brackets to the
    /// declaration's semicolon, so an array, a ternary, a collection initializer or a lambda is
    /// inside the span rather than truncating it.
    /// </summary>
    /// <remarks>
    /// A static <em>method</em> cannot match: the field pattern forbids braces before the <c>=</c>,
    /// so it cannot reach past a method's parameter list into its body, and the property pattern
    /// requires an <c>=</c> immediately after the accessor block. Either way a local built fresh on
    /// every call - which is not the hazard - stays out.
    /// </remarks>
    private static IEnumerable<(int Index, string Declaration, string Initializer)> StaticInitializers(string text)
    {
        foreach (Match declaration in StaticFieldOrProperty.Matches(text))
        {
            int i = declaration.Index + declaration.Length;
            int depth = 0;
            int start = i;

            while (i < text.Length)
            {
                char c = text[i];
                if (c is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (c is ')' or ']' or '}')
                {
                    depth--;
                }
                else if (c == ';' && depth == 0)
                {
                    break;
                }

                i++;
            }

            yield return (declaration.Index, declaration.Value, text[start..Math.Min(i, text.Length)]);
        }
    }

    /// <summary>
    /// Comments and literals blanked, so a type named in prose or in a string is not a finding.
    /// Length and offsets are preserved, which is what keeps the reported line numbers honest.
    /// </summary>
    private static string WithoutCommentsAndLiterals(string text)
    {
        var buffer = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != (char)10) { buffer.Append(text[i] == (char)10 ? text[i] : ' '); i++; }
                if (i < text.Length) { buffer.Append(text[i]); }
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    buffer.Append(text[i] == (char)10 ? text[i] : ' ');
                    i++;
                }

                buffer.Append("  ");
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                char quote = c;
                bool verbatim = quote == '"' && i > 0 && text[i - 1] == '@';
                buffer.Append(' ');
                i++;
                while (i < text.Length)
                {
                    if (!verbatim && text[i] == '\\') { buffer.Append("  "); i += 2; continue; }
                    if (text[i] == quote)
                    {
                        if (verbatim && i + 1 < text.Length && text[i + 1] == quote) { buffer.Append("  "); i += 2; continue; }
                        break;
                    }

                    buffer.Append(text[i] == (char)10 ? text[i] : ' ');
                    i++;
                }

                buffer.Append(' ');
                continue;
            }

            buffer.Append(c);
        }

        return buffer.ToString();
    }

    /// <summary>
    /// The mutable, <c>AvaloniaObject</c>-derived drawing types. The <c>Immutable</c> prefixed
    /// counterparts are deliberately absent - they are the fix, not the hazard - and the leading
    /// word boundary is what keeps <c>ImmutableSolidColorBrush</c> from matching
    /// <c>SolidColorBrush</c>.
    /// </summary>
    private const string ThreadAffineDrawingTypes =
        "SolidColorBrush|LinearGradientBrush|RadialGradientBrush|ConicGradientBrush"
        + "|ImageBrush|VisualBrush|DrawingBrush|Pen";

    /// <summary>A static field, or a static property whose accessor block is followed by <c>=</c>.</summary>
    private static readonly Regex StaticFieldOrProperty = new(
        @"\bstatic\b[^;{}=]*?=" + @"|\bstatic\b[^;{}=]*?\{[^{}]*\}\s*=",
        RegexOptions.Compiled);

    /// <summary>The declared type naming one of those types, for the target-typed case.</summary>
    private static readonly Regex AffineTypeName = new(
        @"\b(?:" + ThreadAffineDrawingTypes + @")\b",
        RegexOptions.Compiled);

    /// <summary>A bare <c>new()</c> or <c>new { }</c>, which takes its type from the declaration.</summary>
    private static readonly Regex TargetTypedNew = new(
        @"^\s*new\s*[({]",
        RegexOptions.Compiled);

    /// <summary>A construction of one of those types, qualified however the author spelled it.</summary>
    private static readonly Regex AffineConstruction = new(
        @"\bnew\s+(?:global::)?(?:[\w.]+\.)?\b(?:" + ThreadAffineDrawingTypes + @")\s*[({]",
        RegexOptions.Compiled);

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
    /// Every category this guard treats as isolating must really be excluded from the shared
    /// headless pass in CI, or the exemption it grants is fictional.
    /// </summary>
    /// <remarks>
    /// The whole point of <see cref="IsolatingCategories"/> is "CI runs this elsewhere, so the
    /// lane trait is unnecessary". That claim lives in a different file from the workflow it
    /// describes, which makes it exactly the kind of assertion that rots quietly. Reading the
    /// workflow is cheap and turns a silent hole into a named failure.
    /// </remarks>
    [Fact]
    public void EveryIsolatingCategoryIsActuallyExcludedInCi()
    {
        string workflow = Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(workflow), $"Expected the CI workflow at {workflow}.");
        string text = File.ReadAllText(workflow);

        var notExcluded = IsolatingCategories
            .Where(category => !text.Contains($"Category!={category}", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            notExcluded.Count == 0,
            "These categories are treated as isolating a test into its own CI process, but "
            + "ci.yml does not exclude them from the shared headless App.Tests pass - so a test "
            + "carrying only one of them still shares a process with the [AvaloniaFact] tests, "
            + "and the exemption this guard grants it is fictional. Either exclude the category "
            + "in ci.yml or drop it from IsolatingCategories: "
            + string.Join(", ", notExcluded));
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
