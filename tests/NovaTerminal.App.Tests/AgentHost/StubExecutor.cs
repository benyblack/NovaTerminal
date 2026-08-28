using System;
using System.Threading.Tasks;
using NovaTerminal.AgentHost;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// Shared <see cref="IAgentActionExecutor"/> stub for act-surface protocol tests
/// (spawnSession / closeSession). Defaults to failing every call until a test
/// wires <see cref="OnSpawn"/> / <see cref="OnClose"/>.
/// </summary>
internal sealed class StubExecutor : IAgentActionExecutor
{
    public Func<string?, (AgentSpawnResult?, AgentSpawnError?)>? OnSpawn;
    public Func<Guid, bool>? OnClose;
    public string? LastSpawnProfile;
    public Guid? LastClosePane;
    public int SpawnCalls;

    public Task<(AgentSpawnResult? Result, AgentSpawnError? Error)> SpawnAsync(string? profileName)
    {
        SpawnCalls++;
        LastSpawnProfile = profileName;
        var r = OnSpawn?.Invoke(profileName) ?? (null, AgentSpawnError.SpawnFailed);
        return Task.FromResult(r);
    }

    public Task<bool> ClosePaneAsync(Guid paneId)
    {
        LastClosePane = paneId;
        return Task.FromResult(OnClose?.Invoke(paneId) ?? false);
    }
}
