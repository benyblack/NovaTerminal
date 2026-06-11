namespace NovaTerminal.Shell.Secrets
{
    public static class SecretStore
    {
        // TEMPORARY stub — replaced in Task 3 with platform selection + legacy cleanup.
        public static ISecretStore CreateDefault() => new InMemorySecretStore();
    }
}
