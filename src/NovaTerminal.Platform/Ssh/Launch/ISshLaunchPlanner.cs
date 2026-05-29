namespace NovaTerminal.Platform.Ssh.Launch;

public interface ISshLaunchPlanner
{
    SshLaunchPlan Plan(Guid profileId, IReadOnlyList<string>? extraArgs = null);
}
