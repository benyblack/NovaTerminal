namespace NovaTerminal.Core.Ssh.Models;

public static class SshProfileNormalizer
{
    public static void NormalizeRememberPasswordPreference(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // Legacy compatibility: older profiles may carry this flag, but runtime
        // password memory is now driven by vault state and prompt interaction.
    }
}
