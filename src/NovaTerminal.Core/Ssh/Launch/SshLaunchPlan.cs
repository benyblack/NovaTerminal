namespace NovaTerminal.Core.Ssh.Launch;

public sealed class SshLaunchPlan
{
    public required Guid ProfileId { get; init; }
    public required string SshExecutablePath { get; init; }
    public required string ConfigFilePath { get; init; }
    public required string Alias { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
}
