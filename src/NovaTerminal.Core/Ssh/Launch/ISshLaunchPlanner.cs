namespace NovaTerminal.Core.Ssh.Launch;

public interface ISshLaunchPlanner
{
    SshLaunchPlan Plan(Guid profileId, IReadOnlyList<string>? extraArgs = null);
}
