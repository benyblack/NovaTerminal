using NovaTerminal.Core.Ssh.Launch;

namespace NovaTerminal.Core.Ssh.Sessions;

public interface ISshSessionFactory
{
    ITerminalSession Create(
        Guid profileId,
        int cols = 120,
        int rows = 30,
        SshDiagnosticsLevel diagnosticsLevel = SshDiagnosticsLevel.None,
        IReadOnlyList<string>? extraArgs = null,
        Action<string>? log = null);
}
