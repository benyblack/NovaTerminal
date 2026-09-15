using System;
using System.Threading.Tasks;
using NovaTerminal.AgentHost;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// Shared <see cref="IAgentActionExecutor"/> stub for the protocol tests that go
/// through the UI-thread bridge (spawnSession / closeSession, and captureScreen's
/// live mode). Defaults to failing every call until a test wires
/// <see cref="OnSpawn"/> / <see cref="OnClose"/> / <see cref="OnCaptureLive"/> —
/// which is also the "no pane on screen" case the live path has to handle.
/// </summary>
internal sealed class StubExecutor : IAgentActionExecutor
{
    public Func<string?, (AgentSpawnResult?, AgentSpawnError?)>? OnSpawn;
    public Func<Guid, bool>? OnClose;
    public Func<Guid, int, double, AgentLiveCapture?>? OnCaptureLive;
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

    public Guid? LastLiveCapturePane;
    public int? LastLiveCaptureMaxWidth;
    public double? LastLiveCaptureScale;

    public Task<AgentLiveCapture?> CaptureLiveAsync(Guid paneId, int maxWidth, double scale)
    {
        LastLiveCapturePane = paneId;
        LastLiveCaptureMaxWidth = maxWidth;
        LastLiveCaptureScale = scale;
        return Task.FromResult(OnCaptureLive?.Invoke(paneId, maxWidth, scale));
    }
}
