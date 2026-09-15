using Ntilde.Platform.Ssh.Launch;
using Ntilde.Pty;

namespace Ntilde.Platform.Ssh.Sessions;

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
