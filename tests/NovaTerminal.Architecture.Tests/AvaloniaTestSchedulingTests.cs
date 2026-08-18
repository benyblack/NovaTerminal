using System;
using System.Collections.Generic;
using System.IO;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// #317: the Avalonia headless platform can only be booted once, by one thread, and whichever
/// thread boots it owns the dispatcher from then on. A test class that boots it from a plain
/// <c>[Fact]</c> constructor — on whatever thread xUnit happened to pick — therefore races the
/// test framework's own initialisation, and the loser throws "The calling thread cannot access
/// this object because a different thread owns it". That is not hypothetical: it made
/// <c>AgentHostCaptureProtocolTests</c> fail in any sweep that also ran other Avalonia tests,
/// which in turn made every broad <c>--filter</c> run unreliable.
///
/// The rule that fixes it is a scheduling one: everything that boots the platform lives in the
/// single non-parallel "GoldenPng" collection, so those classes can never run alongside each
/// other. A source scan rather than a runtime check, because by the time the race has happened
/// the damage is a confusing failure in an unrelated test.
///
/// Note the rule is about the *collection*, not about using <c>[AvaloniaFact]</c>: moving
/// pipe-I/O tests onto the framework's dispatcher thread stalled whole sweeps, so
/// <c>AgentHostCaptureProtocolTests</c> deliberately stays on <c>[Fact]</c>.
/// </summary>
public class AvaloniaTestSchedulingTests
{
    // A call site, not the bare identifier: this file names the method in its own prose,
    // and a guard that flags itself is worse than no guard.
    private const string Booter = "SnapshotService.EnsureAvaloniaInitialized(";
    private const string RequiredCollection = "[Collection(\"GoldenPng\")]";

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

    [Fact]
    public void EveryTestThatBootsAvaloniaIsInTheSerializedCollection()
    {
        string tests = Path.Combine(RepoRoot(), "tests");
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            // Skip build output: bin/obj carry copies of sources under some SDK versions.
            string relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (!text.Contains(Booter, StringComparison.Ordinal))
            {
                continue;
            }

            // Two files mention the call without making it: the helper that declares it, and
            // this guard, whose needle and prose both contain it. A scan that flags its own
            // source is a guard nobody can keep green.
            string name = Path.GetFileName(file);
            if (name == "SnapshotService.cs" || name == "AvaloniaTestSchedulingTests.cs")
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
            "These test files boot the Avalonia headless platform but are not in the serialized "
            + "\"GoldenPng\" collection, so they can race another Avalonia test's initialisation (#317): "
            + string.Join(", ", offenders));
    }
}
