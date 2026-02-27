using NovaTerminal.Core.Ssh.Models;

namespace NovaTerminal.Core.Ssh.OpenSsh;

public interface IOpenSshConfigCompiler
{
    OpenSshCompilationResult Compile(IReadOnlyList<SshProfile> profiles, Guid launchProfileId);
}

public sealed class OpenSshCompilationResult
{
    public required string ConfigFilePath { get; init; }
    public required string Alias { get; init; }
}

public sealed class OpenSshCompilerOptions
{
    public bool IsolateKnownHosts { get; init; }
    public string? KnownHostsFilePath { get; init; }
}
