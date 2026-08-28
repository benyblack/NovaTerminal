using NovaTerminal.Shots.Scenarios;

namespace NovaTerminal.Shots;

public static class ScenarioCatalog
{
    private static readonly IScenario[] Scenarios =
    [
        new HeroSingleScenario(),
        new SettingsAgentAccessScenario(),
        new AgentSessionScenario(),
        new ClipAgentScenario()
    ];

    public static IReadOnlyList<IScenario> All() => Scenarios;

    public static IScenario? Find(string name) =>
        Scenarios.FirstOrDefault(s => string.Equals(s.Spec.Name, name, StringComparison.OrdinalIgnoreCase));
}
