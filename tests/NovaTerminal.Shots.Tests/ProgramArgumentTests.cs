using System.Globalization;
using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

/// <summary>
/// Covers <see cref="Program.ResolveScenarios"/> and <see cref="Program.ResolveScale"/> - the
/// argument-shape bug where every <c>--</c>-prefixed token, boolean switches included, was
/// treated as consuming the next token as its value. See Program.cs's own remarks on
/// <c>ValueTakingFlags</c> for the full story: <c>shots.ps1 --publish hero-single</c> used to
/// skip <c>hero-single</c> as if it were <c>--publish</c>'s argument, resolve to an empty name
/// list, and fall back to running (and, with <c>--prune</c>, pruning down to) the whole catalogue.
/// </summary>
public sealed class ProgramArgumentTests
{
    [Fact]
    public void BooleanFlagBeforeName_StillResolvesTheName()
    {
        // The exact shape from the bug report: a boolean switch immediately followed by a
        // scenario name must not eat that name as its own value.
        Program.ScenarioResolution resolution = Program.ResolveScenarios(["--publish", "hero-single"]);

        Assert.Equal(["hero-single"], resolution.Scenarios.Select(s => s.Spec.Name));
        Assert.Empty(resolution.UnknownNames);
    }

    [Fact]
    public void BooleanFlagAfterName_StillResolvesTheName()
    {
        Program.ScenarioResolution resolution = Program.ResolveScenarios(["hero-single", "--publish"]);

        Assert.Equal(["hero-single"], resolution.Scenarios.Select(s => s.Spec.Name));
        Assert.Empty(resolution.UnknownNames);
    }

    [Fact]
    public void ValueTakingFlag_StillSkipsItsOwnValueOnly()
    {
        // --scale's value ("2") must be skipped, but the name on either side of it must still
        // resolve - the fix must not regress the one case the old skip-after-every-"--" logic
        // was actually built for.
        Program.ScenarioResolution resolution =
            Program.ResolveScenarios(["hero-single", "--scale", "2", "--publish"]);

        Assert.Equal(["hero-single"], resolution.Scenarios.Select(s => s.Spec.Name));
        Assert.Empty(resolution.UnknownNames);
    }

    [Fact]
    public void UnknownName_IsReportedRatherThanSilentlyDropped()
    {
        Program.ScenarioResolution resolution = Program.ResolveScenarios(["hero-single", "no-such-shot"]);

        Assert.Equal(["hero-single"], resolution.Scenarios.Select(s => s.Spec.Name));
        Assert.Equal(["no-such-shot"], resolution.UnknownNames);
    }

    [Fact]
    public void NoArguments_ResolvesToTheWholeCatalogue()
    {
        Program.ScenarioResolution resolution = Program.ResolveScenarios([]);

        Assert.Equal(ScenarioCatalog.All().Count, resolution.Scenarios.Count);
        Assert.Empty(resolution.UnknownNames);
    }

    [Fact]
    public void Scale_ParsesADecimalValueUnderInvariantCulture()
    {
        double scale = Program.ResolveScale(["--scale", "1.5"]);

        Assert.Equal(1.5, scale);
    }

    [Fact]
    public void Scale_ParsesADecimalValueEvenUnderACommaDecimalCulture()
    {
        // A culture-sensitive double.TryParse (e.g. under "de-DE", where "," is the decimal
        // separator) would fail to parse "1.5" and silently fall back to the 2.0 default instead
        // of the value actually requested. CultureInfo.InvariantCulture must be what is used
        // regardless of the thread's current culture.
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            double scale = Program.ResolveScale(["--scale", "1.5"]);

            Assert.Equal(1.5, scale);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Scale_DefaultsTo2WhenNotProvided()
    {
        double scale = Program.ResolveScale(["hero-single", "--publish"]);

        Assert.Equal(2.0, scale);
    }
}
