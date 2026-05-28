namespace NovaTerminal.Tests.CommandAssist.ShellIntegration.Integration;

/// <summary>
/// All shell-integration test classes share this collection so xunit
/// runs them sequentially. Each PTY session pins a ReadLoop + ProcessLoop
/// thread; running multiple classes in parallel on a 2-core CI runner
/// starves the ThreadPool and the AnsiParser callback that signals
/// prompt-ready cannot be scheduled in time, causing cascading hangs.
/// </summary>
[CollectionDefinition(nameof(ShellIntegrationCollection), DisableParallelization = true)]
public sealed class ShellIntegrationCollection
{
}
