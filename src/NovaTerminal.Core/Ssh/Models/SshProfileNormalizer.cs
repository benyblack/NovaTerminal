namespace NovaTerminal.Core.Ssh.Models;

public static class SshProfileNormalizer
{
    public static void NormalizeRememberPasswordPreference(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.BackendKind != SshBackendKind.Native)
        {
            profile.RememberPasswordInVault = false;
        }
    }
}
