namespace Ntilde.Platform.Tests.Ssh;

/// <summary>
/// Serializes the tests that mutate <c>NTILDE_APPDATA_ROOT</c>. The variable is process-wide, so
/// two of them running at once would each see the other's value.
/// </summary>
[CollectionDefinition(nameof(SshPathSandboxCollection), DisableParallelization = true)]
public sealed class SshPathSandboxCollection
{
}
