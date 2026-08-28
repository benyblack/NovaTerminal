using NovaTerminal.Shots;

namespace NovaTerminal.ShotsTests;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void EveryScenarioHasAUniqueName()
    {
        string[] names = ScenarioCatalog.All().Select(s => s.Spec.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryScenarioStatesItsIntent()
    {
        foreach (IScenario scenario in ScenarioCatalog.All())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(scenario.Spec.Intent),
                $"Scenario '{scenario.Spec.Name}' has no Intent. Claude reads Intent to judge whether " +
                "the produced image is right, so a blank one makes the review step useless.");
        }
    }

    [Fact]
    public void Find_ReturnsTheNamedScenario()
    {
        Assert.Equal("hero-single", ScenarioCatalog.Find("hero-single")!.Spec.Name);
        Assert.Null(ScenarioCatalog.Find("no-such-shot"));
    }
}
